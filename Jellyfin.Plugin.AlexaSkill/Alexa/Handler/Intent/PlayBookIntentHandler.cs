using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayBookIntent — searches for audiobooks and plays their audio content.
/// Resumes from the last position when the user has existing progress.
/// </summary>
public class PlayBookIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager _queueManager;

    public PlayBookIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager queueManager) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(
            intentRequest.Intent.Name, IntentNames.PlayBook, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(
        Request request,
        Context context,
        Entities.User user,
        SessionInfo session,
        CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        var disabled = IfFeatureDisabled(c => c.BooksEnabled, request);
        if (disabled != null)
        {
            return disabled;
        }

        IntentRequest intentRequest = (IntentRequest)request;

        string? book = intentRequest.Intent.Slots?.TryGetValue("book", out var bookSlot) == true
            ? bookSlot.Value
            : null;

        if (string.IsNullOrWhiteSpace(book))
        {
            return ResponseBuilder.Ask(
                ResponseStrings.Get("ElicitBookName", locale),
                new Reprompt(ResponseStrings.Get("ElicitBookName", locale)));
        }

        // JF-643: the book title feeds the SearchTerm query and the fuzzy cascade
        // below, both against Latin library names; romanize the query once.
        book = Util.KatakanaRomanizer.Romanize(book);

        RunFireAndForget(SendProgressiveResponse(
            context, request, ResponseStrings.Get("SearchingBook", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var bookQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = book,
            IncludeItemTypes = new[] { BaseItemKind.AudioBook },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(bookQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> books = await RetryAsync(
            () => _libraryManager.GetItemList(bookQuery),
            "GetAudiobooks",
            cancellationToken).ConfigureAwait(false);

        if (books.Count == 0)
        {
            var fuzzy = await Search.SearchItemsFuzzyAsync(book, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.AudioBook }, cancellationToken, "PlayBookFuzzyFallback", locale: locale).ConfigureAwait(false);
            if (fuzzy != null)
            {
                books = new List<BaseItem> { fuzzy.Value.Item };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundBook", locale, book));
            }
        }

        if (books.Count > 1)
        {
            BaseItem? bookMatch = null;
            // JF-776: scoring through the romaji reading (ScoringName), speech
            // keeps the display name (the JF-755 speechSelector seam).
            var (missOutcome, missResponse) = await HandleFuzzyMiss(
                book,
                books,
                b => Util.KeywordMatcher.ScoringName(b.Name),
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeAlbum,
                locale,
                best =>
                {
                    bookMatch = best;
                    return Task.FromResult<SkillResponse>(null!);
                },
                user: user,
                speechSelector: b => b.Name).ConfigureAwait(false);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                if (missResponse != null)
                {
                    return missResponse;
                }

                books = new List<BaseItem> { bookMatch! };
            }
            else
            {
                var matches = books.Take(3).Select(b => (b.Id, b.Name, (string?)Launch.GetImageUrl(b.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(
                    matches, DisambiguationHelper.MediaTypeAlbum, locale, context);
            }
        }

        // Initial page through the ONE shared chapters-page query (JF-670): the
        // continuation fetcher builds the same shape, so head and tail concatenate
        // in one order and cannot drift apart.
        // JF-673: unknownTotalOnFallback because this page drives a pagination loop.
        // On NRE-class servers the page arrives through the GetItemList fallback,
        // whose honest total is UNKNOWN: a page-size total would read as "complete"
        // here, the continuation store below would never engage, and the book would
        // truncate at this page exactly on the servers the NRE guard exists for.
        QueryResult<BaseItem> bookTracks = await RetryAsync(
            () => Search.SafeGetItemsResult(_libraryManager,
                QueueContinuationFetcher.BuildAudiobookChaptersQuery(
                    jellyfinUser, books[0].Id, 0, ProgressiveQueueConstants.GetInitialFetchSize()),
                unknownTotalOnFallback: true),
            "GetBookTracks",
            cancellationToken).ConfigureAwait(false);

        // Single-file audiobooks: the AudioBook item IS the audio track itself.
        // Multi-file audiobooks: children tracks exist under a parent folder.
        // JF-673: the zero check is regime-aware (the ONE predicate shared with the
        // album head, QueueContinuationFetcher.PageHasNoItems).
        IReadOnlyList<BaseItem> trackItems;
        if (QueueContinuationFetcher.PageHasNoItems(bookTracks))
        {
            if (books[0].MediaType == MediaType.Audio)
            {
                trackItems = new List<BaseItem> { books[0] };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NoContentInBook", locale, book));
            }
        }
        else
        {
            trackItems = bookTracks.Items;
        }

        // Check for existing progress — resume from the last position
        Logger.LogDebug(
            "PlayBook: checking resume for '{BookName}' ({BookId}) with {TrackCount} tracks",
            books[0].Name, books[0].Id, trackItems.Count);

        for (int debugIdx = 0; debugIdx < trackItems.Count; debugIdx++)
        {
            UserItemData? dbgData = _userDataManager.GetUserData(jellyfinUser!, trackItems[debugIdx]);
            Logger.LogDebug(
                "PlayBook track[{Idx}]: '{TrackName}' — Played={Played}, PositionTicks={Ticks}",
                debugIdx, trackItems[debugIdx].Name,
                dbgData?.Played, dbgData?.PlaybackPositionTicks);
        }

        (int startIndex, long resumeTicks) = ResumeMath.FindResumeTrackIndex(
            trackItems, jellyfinUser!, _userDataManager, _queueManager, session.DeviceId, resumePosition: true, Logger);

        Logger.LogInformation(
            "PlayBook: FindResumeTrackIndex returned startIndex={StartIndex}, resumeTicks={Ticks} for '{BookName}'",
            startIndex, resumeTicks, books[0].Name);

        int offsetMs = 0;

        if (startIndex > 0 || resumeTicks > 0)
        {
            offsetMs = (int)TimeSpan.FromTicks(resumeTicks).TotalMilliseconds;

            Logger.LogInformation(
                "PlayBook: resuming {Book} from track {Index} ({TrackName}) at {OffsetMs}ms",
                books[0].Name,
                startIndex,
                trackItems[startIndex].Name,
                offsetMs);
        }
        else
        {
            Logger.LogInformation("PlayBook: starting '{Book}' from the beginning (no resume position found)", books[0].Name);
        }

        // JF-693 (the JF-687 refusal-before-ledger policy extended to the queue and
        // session writes): the launch branches below can answer the empty-secret
        // configuration Tell instead of a directive (the audiobook concat URL is
        // always token-gated), so the now-playing queue, the device queue and the
        // progressive-continuation record are applied AFTER the launch verdict: a
        // refused launch must not leave MediaInfo answering "playing <book>" with
        // nothing playing and a stale QueueContinuation that survives.
        void ApplyBookPlaybackState()
        {
            List<QueueItem> queueItems = new();
            for (int i = startIndex; i < trackItems.Count; i++)
            {
                queueItems.Add(new QueueItem { Id = trackItems[i].Id });
            }

            session.NowPlayingQueue = queueItems;
            session.FullNowPlayingItem = trackItems[startIndex];

            _queueManager?.SetQueue(
                context.System.Device.DeviceID,
                trackItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
                0);

            // Store continuation info so PlaybackNearlyFinished can fetch the rest.
            // StartIndex uses the original page size because the database offset is
            // independent of the resume slice.
            // JF-673: the gate is regime-aware (the ONE decision shared with the
            // album head, see QueueContinuationFetcher.InitialPageHasMore).
            if (QueueContinuationFetcher.InitialPageHasMore(bookTracks))
            {
                QueueContinuationStore.Set(
                    session.UserId,
                    context.System.Device.DeviceID,
                    new QueueContinuation
                    {
                        SourceType = "Audiobook",
                        ParentId = books[0].Id,
                        StartIndex = bookTracks.Items.Count,
                        TotalCount = bookTracks.TotalRecordCount,
                        UserId = jellyfinUser!.Id,
                        // JF-674: bind the entry to THIS queue page (the ids just
                        // installed into session.NowPlayingQueue) so a later
                        // different-queue playback discards it at fetch time. Minted
                        // on ALL launch arms, VideoApp included: the identity
                        // validation already discards the entry for any later queue
                        // that is not the book page, while the chapter-relative
                        // cold-tracker resume fallthrough (ResumeIntentHandler) can
                        // still legitimately serve it through an AudioPlayer chapter
                        // relaunch of the SAME queue.
                        MintedQueueItemIds = QueueContinuation.QueueIdsOf(queueItems)
                    });
            }
        }

        string itemId = trackItems[startIndex].Id.ToString();

        // Native controls for books: play via the VideoApp concat stream (seek bar).
        // With a TRACKED position, resume via the ?start= slice (the tracker records
        // the book-absolute concat timeline); a cold tracker falls through to the flat
        // chapter resume (a chapter-relative position cannot slice the book timeline).
        if (Plugin.Instance?.Configuration?.NativeControlsForBooks == true)
        {
            string bookKey = ResumeMath.GetAudiobookBookKey(trackItems[startIndex]);
            // Review major (JF-567): only the TRACKER's book-timeline position may
            // slice the concat playlist; the FindResumeTrackIndex fallback is
            // CHAPTER-relative and would land mid-chapter-1 on the book timeline.
            // A cold tracker falls through to the flat AudioPlayer chapter resume.
            long trackedTicks = Plugin.Instance?.AudiobookPositionTracker?.GetPositionTicks(bookKey) ?? 0;
            if (trackedTicks > 0)
            {
                // JF-567: VideoApp.Launch responses must OMIT shouldEndSession (the repo
                // reference rule; BuildAudiobookResumeResponse keeps it null).
                // JF-699 item 1: the builder either threw the StreamTokenNotConfigured
                // refusal (RequestPipeline answers it; nothing below runs) or delivered
                // the launch, so the JF-693 verdict wrapper is gone and the state/announce
                // writes simply follow the launch.
                SkillResponse trackedResponse = Launch.BuildAudiobookResumeResponse(trackItems[startIndex], trackedTicks, user, context);

                ApplyBookPlaybackState();
                PlaybackLaunchBuilder.AttachAnnounceIfLaunched(
                    trackedResponse,
                    SpeechBuilder.BuildOutputSpeech(
                        "ResumingBookSsml", "ResumingBook", locale, books[0].Name, trackItems[startIndex].Name));
                return trackedResponse;
            }

            // Cold tracker: resumeTicks == 0 covers TWO shapes - a genuinely fresh
            // book, and the (lastPlayedIndex+1, 0) shape where earlier chapters are
            // fully played and the next is unstarted (FindResumeTrackIndex returns
            // ticks 0 there; startIndex > 0 is silently discarded by the fresh
            // launch). Both keep the fresh VideoApp launch (the flag exists for
            // the seek bar); a chapter WITH progress falls through to the flat
            // chapter resume below, where the chapter-relative position is honest
            // on the chapter's own timeline (the same discipline the LaunchRequest
            // resume offer applies).
            if (resumeTicks <= 0)
            {
                SkillResponse freshResponse = await Launch.BuildAudiobookVideoAppLaunchResponseAsync(
                    itemId,
                    trackItems[startIndex],
                    SpeechBuilder.BuildNowPlayingSpeech(books[0].Name, locale, Launch.GetAnnounceNowPlaying(user)),
                    user,
                    context,
                    request).ConfigureAwait(false);

                // JF-699 item 1: throw-or-launch (see the tracked arm above).
                ApplyBookPlaybackState();
                return freshResponse;
            }
        }

        SkillResponse standardResponse = Launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId, trackItems[startIndex], user, context, offsetMs);

        // JF-699 item 1: throw-or-launch (see the tracked arm above); the state
        // writes and the resume announce simply follow the launch.
        ApplyBookPlaybackState();

        // Add resume announcement when not starting from the beginning
        if (startIndex > 0 || resumeTicks > 0)
        {
            PlaybackLaunchBuilder.AttachAnnounceIfLaunched(
                standardResponse,
                SpeechBuilder.BuildOutputSpeech(
                    "ResumingBookSsml", "ResumingBook", locale, books[0].Name, trackItems[startIndex].Name));
            standardResponse.Response.ShouldEndSession = true;
        }

        return standardResponse;
    }
}

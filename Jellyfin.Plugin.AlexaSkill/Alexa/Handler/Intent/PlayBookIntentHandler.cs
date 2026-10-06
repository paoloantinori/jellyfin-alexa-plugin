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

    /// <summary>
    /// Map the audiobook search candidates onto BOOK granularity (JF-793 Finding 3):
    /// each candidate through <see cref="AudiobookItems.TryResolveBookFolder"/>,
    /// deduped by resolved id, preserving first-occurrence order. Chapter leaves
    /// collapse onto their book folder, so the multi-match disambiguation presents
    /// ONE entry per book (named for the book, whose confirm payload is the folder
    /// id, and whose name clears the auto-play bar the "- Chapter N" tails dragged
    /// below) instead of N chapter-granular choices. Single-file books, failed
    /// climbs, and the shared-container rejection (the collapsed book under a
    /// container) keep their own entry: they ARE distinct books. The
    /// post-disambiguation climb in <see cref="HandleAsync"/> stays as the tolerant
    /// safety net (on a Folder the helper harmlessly returns null).
    /// </summary>
    private IReadOnlyList<BaseItem> NormalizeBookCandidates(IReadOnlyList<BaseItem> candidates)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        Dictionary<Guid, BaseItem> byBook = new();
        foreach (BaseItem candidate in candidates)
        {
            BaseItem book = AudiobookItems.TryResolveBookFolder(candidate, _libraryManager) ?? candidate;
            byBook.TryAdd(book.Id, book);
        }

        return byBook.Count == candidates.Count ? candidates : byBook.Values.ToList();
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

        // JF-793 Finding 3: BOOK-granular candidates before the disambiguation
        // consumers (the illusory chapter-granular choice, and the "- Chapter N"
        // name tails dragging the fuzzy scores below the auto-play bar). The shape
        // rationale and the pass-through list live on NormalizeBookCandidates.
        books = NormalizeBookCandidates(books);

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

        // JF-791: the match above for a multi-chapter book is a CHAPTER leaf (the
        // shape rationale and the null contract live on
        // AudiobookItems.TryResolveBookFolder); re-point it at the book folder so
        // every head-query/continuation/name read below runs on the BOOK. Null keeps
        // the match itself: its own track (the JF-361 duality in the zero-children
        // branch below).
        if (AudiobookItems.TryResolveBookFolder(books[0], _libraryManager) is { } bookFolder)
        {
            Logger.LogDebug(
                "PlayBook: match '{MatchName}' is an audiobook chapter leaf, climbing to book folder '{BookName}' ({BookId})",
                books[0].Name, bookFolder.Name, bookFolder.Id);
            books = new List<BaseItem> { bookFolder };
        }

        // Initial page through the ONE shared chapters-page query's scoped sibling
        // (JF-670; JF-767 joined the scope axis so head, confirm, and tail all run
        // the same JF-666 pairing and cannot drift apart): the continuation fetcher
        // and the YesIntent PlayBook confirm build the same shape.
        // JF-673: unknownTotalOnFallback because this page drives a pagination loop.
        // On NRE-class servers the page arrives through the GetItemList fallback,
        // whose honest total is UNKNOWN: a page-size total would read as "complete"
        // here, the continuation store below would never engage, and the book would
        // truncate at this page exactly on the servers the NRE guard exists for.
        QueryResult<BaseItem> bookTracks = await RetryAsync(
            () => Search.SafeGetItemsResult(_libraryManager,
                QueueContinuationFetcher.BuildScopedAudiobookChaptersQuery(
                    jellyfinUser, user, _libraryManager, Logger, books[0].Id,
                    startIndex: 0, limit: ProgressiveQueueConstants.GetInitialFetchSize()),
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

        // The continuation bookkeeping, computed here so the deep-resume block below
        // can rebase it: how many chapters the queue has consumed (page start +
        // page count; the database offset is independent of the resume slice) and
        // whether more remain. JF-673: the gate is regime-aware (the ONE decision
        // shared with the album head, see QueueContinuationFetcher.InitialPageHasMore).
        int continuationStartIndex = bookTracks.Items.Count;
        int continuationTotalCount = bookTracks.TotalRecordCount;
        bool continuationHasMore = QueueContinuationFetcher.InitialPageHasMore(bookTracks);

        // JF-793 Finding 4: the page-1-bounded resume. FindResumeTrackIndex scanned
        // only the initial page, so UserData progress on a chapter BEYOND the page
        // (chapter 22 of 26) was invisible and the fresh ask relaunched from chapter
        // 1 at 0:00 (pre-JF-791 the same ask played the matched chapter at its
        // position, then silence; the album precedent is not liftable, JF-625
        // criterion 3 is the video-route tracker override). The bounded resolution:
        // when page 1 yields no position AND the book extends beyond the page, fetch
        // the book once unpaged (the concat endpoint's fetch-all shape, JF-784) and
        // re-run the ONE resume decision on the full chapter list; a position found
        // beyond the page re-slices the page at that chapter, so the launch, the
        // queue, and the continuation all start at the position-holding chapter.
        // Fresh books and books the page already covers pay nothing (the guard skips
        // the fetch when the page holds the whole book); a multi-page fresh ask pays
        // one extra query. The single-file shapes never reach here (their page is
        // the whole book by construction).
        if (startIndex == 0 && resumeTicks == 0 && continuationHasMore)
        {
            QueryResult<BaseItem> fullBook = await RetryAsync(
                () => Search.SafeGetItemsResult(_libraryManager,
                    QueueContinuationFetcher.BuildScopedAudiobookChaptersQueryUnpaged(
                        jellyfinUser, user, _libraryManager, Logger, books[0].Id)),
                "GetBookTracksDeepResume",
                cancellationToken).ConfigureAwait(false);

            (int deepIndex, long deepTicks) = ResumeMath.FindResumeTrackIndex(
                fullBook.Items, jellyfinUser!, _userDataManager, _queueManager, session.DeviceId, resumePosition: true, Logger);
            if (deepIndex > 0)
            {
                // The re-sliced page starts exactly at the position-holding chapter,
                // so the page-relative answer is (0, deepTicks) by construction in
                // every return shape of FindResumeTrackIndex (in-progress hit, cached
                // position, after-last-played): no re-scan of the slice.
                trackItems = fullBook.Items
                    .Skip(deepIndex)
                    .Take(ProgressiveQueueConstants.GetInitialFetchSize())
                    .ToList();
                startIndex = 0;
                resumeTicks = deepTicks;

                Logger.LogInformation(
                    "PlayBook: deep resume found chapter {DeepIndex} ('{TrackName}') beyond the initial page; re-paging the book at it",
                    deepIndex, fullBook.Items[deepIndex].Name);

                // The fetch-all list is itself the honest total in both regimes
                // (known-total pages and the JF-673 end-unknown fallback), so the
                // continuation carries a real count.
                continuationStartIndex = deepIndex + trackItems.Count;
                continuationTotalCount = fullBook.Items.Count;
                continuationHasMore = continuationStartIndex < continuationTotalCount;
            }
        }

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
            // StartIndex counts the chapters the queue has consumed (page start +
            // page count; the database offset is independent of the resume slice,
            // and the JF-793 deep resume rebases it to the position-holding chapter's
            // page).
            if (continuationHasMore)
            {
                QueueContinuationStore.Set(
                    session.UserId,
                    context.System.Device.DeviceID,
                    new QueueContinuation
                    {
                        SourceType = "Audiobook",
                        ParentId = books[0].Id,
                        StartIndex = continuationStartIndex,
                        TotalCount = continuationTotalCount,
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

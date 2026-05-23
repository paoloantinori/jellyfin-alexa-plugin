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

        await SendProgressiveResponse(
            context, request, ResponseStrings.Get("SearchingBook", locale)).ConfigureAwait(false);

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
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundBook", locale, book));
        }

        if (books.Count > 1)
        {
            BaseItem? bookMatch = null;
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                book,
                books,
                b => b.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeAlbum,
                locale,
                best =>
                {
                    bookMatch = best;
                    return null!;
                },
                user: user);

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
                var matches = books.Take(3).Select(b => (b.Id, b.Name, (string?)GetImageUrl(b.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(
                    matches, DisambiguationHelper.MediaTypeAlbum, locale, context);
            }
        }

        QueryResult<BaseItem> bookTracks = await RetryAsync(
            () => SafeGetItemsResult(_libraryManager, new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                ParentId = books[0].Id,
                MediaTypes = new[] { MediaType.Audio },
                DtoOptions = new DtoOptions(true),
                Limit = ProgressiveQueueConstants.GetInitialFetchSize()
            }),
            "GetBookTracks",
            cancellationToken).ConfigureAwait(false);

        // Single-file audiobooks: the AudioBook item IS the audio track itself.
        // Multi-file audiobooks: children tracks exist under a parent folder.
        IReadOnlyList<BaseItem> trackItems;
        if (bookTracks.TotalRecordCount == 0)
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

        (int startIndex, long resumeTicks) = FindResumeTrackIndex(
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
        if (bookTracks.TotalRecordCount > bookTracks.Items.Count)
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
                    UserId = jellyfinUser!.Id
                });
        }

        string itemId = trackItems[startIndex].Id.ToString();
        SkillResponse response = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, trackItems[startIndex], user, context, offsetMs);

        // Add resume announcement when not starting from the beginning
        if (startIndex > 0 || resumeTicks > 0)
        {
            string bookName = EscapeXml(books[0].Name);
            string trackName = EscapeXml(trackItems[startIndex].Name);
            string? ssml = GetSsml("ResumingBookSsml", locale, bookName, trackName);

            response.Response.OutputSpeech = ssml != null
                ? new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" }
                : new PlainTextOutputSpeech
                {
                    Text = ResponseStrings.Get("ResumingBook", locale, books[0].Name, trackItems[startIndex].Name)
                };
            response.Response.ShouldEndSession = true;
        }

        return response;
    }
}

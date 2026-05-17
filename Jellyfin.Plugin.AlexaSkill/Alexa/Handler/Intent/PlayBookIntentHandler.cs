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
/// </summary>
public class PlayBookIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly DeviceQueueManager? _queueManager;

    public PlayBookIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
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
                var matches = books.Take(3).Select(b => (b.Id, b.Name)).ToList();
                return DisambiguationHelper.AskFirstMatch(
                    matches, DisambiguationHelper.MediaTypeAlbum, locale);
            }
        }

        QueryResult<BaseItem> bookTracks = await RetryAsync(
            () => _libraryManager.GetItemsResult(new InternalItemsQuery
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

        if (bookTracks.TotalRecordCount == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoContentInBook", locale, book));
        }

        IReadOnlyList<BaseItem> trackItems = bookTracks.Items;

        List<QueueItem> queueItems = new();
        for (int i = 0; i < trackItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = trackItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = trackItems[0];

        _queueManager?.SetQueue(
            context.System.Device.DeviceID,
            trackItems.Select(i => i.Id.ToString()).ToList(),
            0);

        if (bookTracks.TotalRecordCount > trackItems.Count)
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Audiobook",
                    ParentId = books[0].Id,
                    StartIndex = trackItems.Count,
                    TotalCount = bookTracks.TotalRecordCount,
                    UserId = jellyfinUser.Id
                });
        }

        string itemId = trackItems[0].Id.ToString();
        return BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, trackItems[0], user, context);
    }
}

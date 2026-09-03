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
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayRandom intents. Plays random media from the user's library.
/// </summary>
public class PlayRandomIntentHandler : BaseHandler
{
    private const int MaxQueryResults = 500;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayRandomIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayRandomIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayRandom, StringComparison.Ordinal);
    }

    /// <summary>
    /// Play random media items from the user's library.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Play directive with random items.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? mediaTypeSlot = null;
        string? genreSlot = null;

        if (intentRequest.Intent.Slots != null)
        {
            if (intentRequest.Intent.Slots.TryGetValue("media_type", out Slot? mediaSlot))
            {
                mediaTypeSlot = mediaSlot.Value;
            }

            if (intentRequest.Intent.Slots.TryGetValue("genre", out Slot? genreSlotObj))
            {
                genreSlot = genreSlotObj.Value;
            }
        }

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            Limit = MaxQueryResults,
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(query, user, _libraryManager);

        BaseItemKind[] playableKinds = ResolvePlayableKinds(mediaTypeSlot);
        if (playableKinds.Length == 0)
        {
            // JF-466: every kind applicable to this request is disabled by content
            // access. Skip the query (an empty IncludeItemTypes would widen it to
            // all types) and speak the shared disabled-type response: the
            // empty-library not-found below would be false ("add music" while
            // music exists but is disabled). The gate sits BEFORE the
            // "searching" announcement (JF-466 review) so a disabled request
            // speaks one message, not two.
            Logger.LogInformation("PlayRandom: all playable kinds for media type '{MediaType}' disabled by configuration", mediaTypeSlot);
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaTypeNotAvailable", locale));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        query.IncludeItemTypes = playableKinds;

        if (!string.IsNullOrWhiteSpace(genreSlot))
        {
            query.Genres = new[] { genreSlot };
        }

        IReadOnlyList<BaseItem> items = await RetryAsync(() => _libraryManager.GetItemList(query), "GetRandomItems", cancellationToken).ConfigureAwait(false);

        if (items.Count == 0)
        {
            string notFoundMsg = !string.IsNullOrWhiteSpace(genreSlot)
                ? ResponseStrings.Get("NotFoundRandomGenre", locale, genreSlot)
                : ResponseStrings.Get("NotFoundRandom", locale);
            return ResponseBuilder.Tell(notFoundMsg);
        }

        // Shuffle items
        List<BaseItem> shuffled = items.ToList();
        Shuffle(shuffled);

        // For albums, expand the first album to tracks
        if (shuffled[0] is MediaBrowser.Controller.Entities.Audio.MusicAlbum album)
        {
            IReadOnlyList<BaseItem> tracks = await ExpandAlbumToTracks(album, jellyfinUser!, cancellationToken).ConfigureAwait(false);
            if (tracks.Count > 0)
            {
                shuffled = tracks.ToList();
                Shuffle(shuffled);
            }
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < shuffled.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = shuffled[i].Id, PlaylistItemId = null });
        }

        session.NowPlayingQueue = queueItems;

        BaseItem firstItem = shuffled[0];
        session.FullNowPlayingItem = firstItem;

        string itemId = firstItem.Id.ToString();

        // Use VideoApp for movies/episodes, AudioPlayer for audio
        if (firstItem is MediaBrowser.Controller.Entities.Movies.Movie
            or MediaBrowser.Controller.Entities.TV.Episode)
        {
            var outputSpeech = BuildNowPlayingSpeech(firstItem.Name, locale, GetAnnounceNowPlaying(user));

            return new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    // VideoApp.Launch must NOT include shouldEndSession
                    ShouldEndSession = null,
                    OutputSpeech = outputSpeech,
                    Directives = new List<IDirective>
                    {
                        new Directive.VideoAppLaunchDirective
                        {
                            VideoItem = new Directive.VideoItem
                            {
                                Source = GetStreamUrl(itemId, user),
                                Metadata = new Directive.VideoItemMetadata { Title = firstItem.Name }
                            }
                        }
                    }
                }
            };
        }

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, firstItem, user, context);
    }

    // JF-466: returns the content-access-filtered kinds for the slot instead of
    // assigning them to the query, so the caller can treat an EMPTY result as a
    // hard zero: an empty IncludeItemTypes means "all kinds" to Jellyfin (verified
    // at 10.11.11), so issuing the query anyway would return items of ANY type
    // (e.g. movies through the audio stream URL when only music is disabled).
    private BaseItemKind[] ResolvePlayableKinds(string? mediaTypeSlot)
    {
        if (string.IsNullOrEmpty(mediaTypeSlot))
        {
            return FilterByContentAccess(new[] { BaseItemKind.Movie, BaseItemKind.Episode });
        }

        if (SlotMappings.MediaTypeToItemKinds.TryGetValue(mediaTypeSlot.ToLowerInvariant(), out BaseItemKind[]? types) && types != null)
        {
            return FilterByContentAccess(types);
        }

        return FilterByContentAccess(new[] { BaseItemKind.Audio });
    }

    private async Task<IReadOnlyList<BaseItem>> ExpandAlbumToTracks(MediaBrowser.Controller.Entities.Audio.MusicAlbum album, Jellyfin.Database.Implementations.Entities.User jellyfinUser, CancellationToken cancellationToken)
    {
        return await RetryAsync(
            () => _libraryManager.GetItemList(new InternalItemsQuery
            {
                User = jellyfinUser,
                ParentId = album.Id,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Recursive = true,
                DtoOptions = new DtoOptions(true)
            }),
            "GetAlbumTracks",
            cancellationToken).ConfigureAwait(false);
    }
}

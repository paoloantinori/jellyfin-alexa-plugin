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
    /// <returns>Play directive with random items.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

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

        Jellyfin.Database.Implementations.Entities.User jellyfinUser = _userManager.GetUserById(session.UserId);

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            Limit = MaxQueryResults,
            DtoOptions = new DtoOptions(true)
        };

        ApplyMediaTypeFilter(query, mediaTypeSlot);

        if (!string.IsNullOrEmpty(genreSlot))
        {
            query.Genres = new[] { genreSlot };
        }

        IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(query);

        if (items.Count == 0)
        {
            string notFoundMsg = !string.IsNullOrEmpty(genreSlot)
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
            IReadOnlyList<BaseItem> tracks = ExpandAlbumToTracks(album, jellyfinUser);
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
            return new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    ShouldEndSession = true,
                    OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get("NowPlaying", locale, firstItem.Name)),
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

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, firstItem, user);
    }

    private static void ApplyMediaTypeFilter(InternalItemsQuery query, string? mediaTypeSlot)
    {
        if (string.IsNullOrEmpty(mediaTypeSlot))
        {
            query.IncludeItemTypes = new[] { BaseItemKind.Audio };
            return;
        }

        switch (mediaTypeSlot.ToLowerInvariant())
        {
            case "video":
                query.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode };
                break;
            default:
                query.IncludeItemTypes = new[] { BaseItemKind.Audio };
                break;
        }
    }

    private IReadOnlyList<BaseItem> ExpandAlbumToTracks(MediaBrowser.Controller.Entities.Audio.MusicAlbum album, Jellyfin.Database.Implementations.Entities.User jellyfinUser)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            User = jellyfinUser,
            ParentId = album.Id,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
            DtoOptions = new DtoOptions(true)
        });
    }

    private static void Shuffle<T>(List<T> list)
    {
        int n = list.Count;
        for (int i = n - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

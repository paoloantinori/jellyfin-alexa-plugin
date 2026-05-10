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
using VideoAppDirective = Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handles AMAZON.YesIntent during search disambiguation.
/// Plays the current match from the disambiguation state.
/// </summary>
public class YesIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="YesIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public YesIntentHandler(
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
        return request is IntentRequest intentRequest
            && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonYes, StringComparison.Ordinal);
    }

    /// <summary>
    /// Handle without session attributes - no disambiguation in progress.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", GetLocale(request))));
    }

    /// <summary>
    /// Handle with session attributes - resolve disambiguation by playing the selected match.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        var state = DisambiguationHelper.ReadState(sessionAttributes);
        if (state == null)
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", locale)));
        }

        var (matches, index, mediaType) = state.Value;
        if (index < 0 || index >= matches.Count)
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", locale)));
        }

        Guid itemId = Guid.Parse(matches[index].Id);
        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return Task.FromResult<SkillResponse>(userError);
        }

        SkillResponse response = mediaType switch
        {
            DisambiguationHelper.MediaTypeSong => PlaySong(item, user, session),
            DisambiguationHelper.MediaTypeAlbum => PlayAlbum(item, jellyfinUser, user, session, locale),
            DisambiguationHelper.MediaTypeArtist => PlayArtist(item, jellyfinUser, user, session, locale),
            DisambiguationHelper.MediaTypeVideo => PlayVideo(item, user, session),
            DisambiguationHelper.MediaTypePlaylist => PlayPlaylist(item, jellyfinUser, user, session, locale),
            _ => ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale))
        };

        return Task.FromResult(response);
    }

    private SkillResponse PlaySong(BaseItem song, Entities.User user, SessionInfo session)
    {
        string itemId = song.Id.ToString();
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = song.Id } };
        session.FullNowPlayingItem = song;
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, song, user);
    }

    private SkillResponse PlayAlbum(BaseItem album, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, SessionInfo session, string locale)
    {
        IReadOnlyList<BaseItem> albumItems = _libraryManager.GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            ParentId = album.Id,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
        });

        if (albumItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsInAlbum", locale, album.Name));
        }

        List<QueueItem> queueItems = albumItems.Select(i => new QueueItem { Id = i.Id }).ToList();
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = albumItems[0];
        string itemId = albumItems[0].Id.ToString();
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, albumItems[0], user);
    }

    private SkillResponse PlayArtist(BaseItem artist, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, SessionInfo session, string locale)
    {
        IReadOnlyList<BaseItem> artistItems = _libraryManager.GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
            ArtistIds = new[] { artist.Id }
        });

        if (artistItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsForArtist", locale, artist.Name));
        }

        List<QueueItem> queueItems = artistItems.Select(i => new QueueItem { Id = i.Id }).ToList();
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = artistItems[0];
        string itemId = artistItems[0].Id.ToString();
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, artistItems[0], user);
    }

    private SkillResponse PlayVideo(BaseItem video, Entities.User user, SessionInfo session)
    {
        string itemId = video.Id.ToString();
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = video.Id } };
        session.FullNowPlayingItem = video;

        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                Directives = new List<IDirective>
                {
                    new VideoAppDirective.VideoAppLaunchDirective
                    {
                        VideoItem = new VideoAppDirective.VideoItem
                        {
                            Source = GetStreamUrl(itemId, user),
                            Metadata = new VideoAppDirective.VideoItemMetadata
                            {
                                Title = video.Name
                            }
                        }
                    }
                }
            }
        };
    }

    private SkillResponse PlayPlaylist(BaseItem playlist, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, SessionInfo session, string locale)
    {
        IReadOnlyList<BaseItem> playlistItems = ((Folder)playlist).GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
        });

        if (playlistItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("PlaylistEmpty", locale));
        }

        List<QueueItem> queueItems = playlistItems.Select(i => new QueueItem { Id = i.Id, PlaylistItemId = playlist.Id.ToString() }).ToList();
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = playlistItems[0];
        string itemId = playlistItems[0].Id.ToString();
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, playlistItems[0], user);
    }
}

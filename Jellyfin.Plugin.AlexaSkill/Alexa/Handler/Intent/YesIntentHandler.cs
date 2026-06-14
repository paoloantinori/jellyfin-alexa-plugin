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
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using VideoAppDirective = Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

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
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Yes: no session attributes, responding with unexpected-yes");
        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", GetLocale(request))));
    }

    /// <summary>
    /// Handle with session attributes - resolve resume confirmation, pagination, or disambiguation.
    /// Resume confirmation takes priority over pagination, which takes priority over disambiguation.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="sessionAttributes">The session attributes containing disambiguation, pagination, or resume state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        // Check for resume confirmation first
        var resumeState = ResumeHelper.ReadState(sessionAttributes);
        if (resumeState != null)
        {
            Logger.LogDebug("Yes: confirming resume, itemId={ItemId}, offsetMs={OffsetMs}", resumeState.ItemId, resumeState.OffsetMs);
            return HandleResumeConfirmation(resumeState, user, session, context, locale);
        }

        // Check for active pagination state - "yes" acts as "show more"
        var paginationState = ListPaginationHelper.ReadState(sessionAttributes);
        if (paginationState != null)
        {
            Logger.LogDebug("Yes: continuing pagination type={ListType}", paginationState.Type);
            return HandlePaginationContinuation(paginationState, context, user, locale);
        }

        var state = DisambiguationHelper.ReadState(sessionAttributes);
        if (state == null)
        {
            Logger.LogDebug("Yes: no disambiguation state, responding with unexpected-yes");
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", locale)));
        }

        var (matches, index, mediaType) = state.Value;
        if (index < 0 || index >= matches.Count)
        {
            Logger.LogDebug("Yes: disambiguation index {Index} out of range (count={MatchCount})", index, matches.Count);
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", locale)));
        }

        Logger.LogDebug("Yes: confirming disambiguation, mediaType={MediaType}, index={Index}, matchCount={MatchCount}", mediaType, index, matches.Count);

        if (!Guid.TryParse(matches[index].Id, out Guid itemId))
        {
            Logger.LogWarning("Invalid GUID format in disambiguation state: {Id}", matches[index].Id);
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

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
            DisambiguationHelper.MediaTypeAlbum => PlayAlbum(item, jellyfinUser!, user, session, locale),
            DisambiguationHelper.MediaTypeArtist => PlayArtist(item, jellyfinUser!, user, session, locale),
            DisambiguationHelper.MediaTypeVideo => PlayVideo(item, user, session),
            DisambiguationHelper.MediaTypePlaylist => PlayPlaylist(item, jellyfinUser!, user, session, locale),
            _ => ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale))
        };

        return Task.FromResult(response);
    }

    /// <summary>
    /// Handle pagination continuation: "yes" acts as "show more" when pagination state is active.
    /// </summary>
    private Task<SkillResponse> HandlePaginationContinuation(
        ListPaginationHelper.PaginationState paginationState,
        Context context,
        Entities.User user,
        string locale)
    {
        return Task.FromResult(ListPaginationHelper.BuildNextPageResponse(_libraryManager, paginationState, locale));
    }

    /// <summary>
    /// Handle resume confirmation: play the stored item from the stored offset.
    /// </summary>
    private Task<SkillResponse> HandleResumeConfirmation(
        ResumeHelper.ResumeState resumeState,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale)
    {
        BaseItem? item = null;
        if (Guid.TryParse(resumeState.ItemId, out Guid itemGuid))
        {
            item = _libraryManager.GetItemById(itemGuid);
        }

        if (item == null)
        {
            Logger.LogWarning("ResumeConfirmation: could not find item {ItemId}", resumeState.ItemId);
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        string itemId = item.Id.ToString();
        session.FullNowPlayingItem = item;

        // Audiobook resume via VideoApp resume playlist (keeps the seek bar). The position
        // is encoded in the playlist via #EXT-X-START; VideoApp.Launch has no offset param.
        if (resumeState.UseResumePlaylist)
        {
            string bookId = (item.ParentId != Guid.Empty ? item.ParentId : item.Id).ToString("N");
            long startTicks = Plugin.Instance?.AudiobookPositionTracker?.GetPositionTicks(bookId) ?? 0;
            if (startTicks <= 0)
            {
                // Tracker cleared between offer and confirm — fall back to the offered offset.
                startTicks = TimeSpan.FromMilliseconds(Math.Min(resumeState.OffsetMs, int.MaxValue)).Ticks;
            }

            SkillResponse response = BuildAudiobookResumeResponse(item, startTicks);
            response.Response.OutputSpeech = _config.ResumeAnnounceTitle
                ? BuildOutputSpeech("ResumingSsml", "Resuming", locale, EscapeXml(item.Name ?? ResponseStrings.Get("UnknownMedia", locale)))
                : BuildOutputSpeech("ResumeBriefSsml", "ResumeBrief", locale);
            return Task.FromResult(response);
        }

        int offsetMs = (int)Math.Min(resumeState.OffsetMs, int.MaxValue);
        SkillResponse standardResponse = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            GetStreamUrl(itemId, user),
            itemId,
            item,
            user,
            context,
            offsetMs);

        // Replace default speech with resume announcement
        if (_config.ResumeAnnounceTitle)
        {
            string title = item.Name ?? ResponseStrings.Get("UnknownMedia", locale);
            standardResponse.Response.OutputSpeech = BuildOutputSpeech("ResumingSsml", "Resuming", locale, EscapeXml(title));
        }
        else
        {
            standardResponse.Response.OutputSpeech = BuildOutputSpeech("ResumeBriefSsml", "ResumeBrief", locale);
        }

        return Task.FromResult(standardResponse);
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
        var artistQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
            ArtistIds = new[] { artist.Id }
        };
        ApplyLibraryFilter(artistQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> artistItems = _libraryManager.GetItemList(artistQuery);

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
                // VideoApp.Launch must NOT include shouldEndSession
                ShouldEndSession = null,
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

using System;
using System.Collections.Generic;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
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
/// Handler for PlaySongIntent requests.
/// </summary>
public class PlaySongIntentHandler : BaseHandler
{
    private ILibraryManager _libraryManager;
    private IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaySongIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlaySongIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "PlaySongIntent", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play songs from a specific artist.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <returns>A skill response.</returns>
    public override SkillResponse Handle(Request request, Context context, Entities.User user, SessionInfo session)
    {
        IntentRequest intentRequest = (IntentRequest)request;
        string songQuery = intentRequest.Intent.Slots["song"].Value;
        string? musicianQuery = intentRequest.Intent.Slots["musician"].Value;

        Jellyfin.Data.Entities.User jellyfinUser = _userManager.GetUserById(session.UserId);

        List<Guid> artistsIds = new List<Guid>();
        if (musicianQuery != null)
        {
            List<BaseItem> artists = _libraryManager.GetItemList(new InternalItemsQuery()
            {
                User = jellyfinUser,
                Recursive = true,
                SearchTerm = musicianQuery,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                DtoOptions = new DtoOptions(true)
            });
            if (artists.Count == 0)
            {
                return ResponseBuilder.Tell(FormattableString.Invariant($"Sorry, I couldn't find any song by the artist {musicianQuery}."));
            }

            foreach (BaseItem artist in artists)
            {
                artistsIds.Add(artist.Id);
            }
        }

        List<BaseItem> songs = _libraryManager.GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = songQuery,
            ArtistIds = artistsIds.ToArray(),
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true)
        });
        if (songs.Count == 0 && musicianQuery != null)
        {
            return ResponseBuilder.Tell(FormattableString.Invariant($"Sorry, I couldn't find any songs with the name {songQuery} by {musicianQuery}."));
        }
        else if (songs.Count == 0)
        {
            return ResponseBuilder.Tell(FormattableString.Invariant($"Sorry, I couldn't find any songs with the name {songQuery}."));
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        // Pick the first match
        queueItems.Add(new QueueItem { Id = songs[0].Id });

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = songs[0];

        string item_id = songs[0].Id.ToString();

        return ResponseBuilder.AudioPlayerPlay(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id);
    }
}

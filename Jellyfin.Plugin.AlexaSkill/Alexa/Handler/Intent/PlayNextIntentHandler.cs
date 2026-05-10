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
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayNextIntent requests.
/// Inserts a song immediately after the currently playing track.
/// </summary>
public class PlayNextIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayNextIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayNextIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayNext, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? songQuery = intentRequest.Intent.Slots?["song"]?.Value;
        string? musicianQuery = intentRequest.Intent.Slots?["musician"]?.Value;

        if (string.IsNullOrWhiteSpace(songQuery))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchQueueItem", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        List<Guid> artistIds = new();
        string? matchedArtistName = null;
        if (!string.IsNullOrWhiteSpace(musicianQuery))
        {
            IReadOnlyList<BaseItem> artists = await RetryAsync(() => _libraryManager.GetItemList(new InternalItemsQuery()
            {
                User = jellyfinUser,
                Recursive = true,
                SearchTerm = musicianQuery,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                DtoOptions = new DtoOptions(true)
            }), "GetArtistsForPlayNext", cancellationToken).ConfigureAwait(false);

            if (artists.Count == 0)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByArtist", locale, musicianQuery));
            }

            matchedArtistName = artists[0].Name;
            foreach (BaseItem artist in artists)
            {
                artistIds.Add(artist.Id);
            }
        }

        IReadOnlyList<BaseItem> songs = await RetryAsync(() => _libraryManager.GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = songQuery,
            ArtistIds = artistIds.ToArray(),
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true)
        }), "GetSongsForPlayNext", cancellationToken).ConfigureAwait(false);

        if (songs.Count == 0)
        {
            return musicianQuery != null
                ? ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByNameAndArtist", locale, songQuery, matchedArtistName!))
                : ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByName", locale, songQuery));
        }

        if (songs.Count > 1)
        {
            BaseItem? topMatch = FuzzyMatch(songQuery, songs, s => s.Name);
            if (topMatch == null)
            {
                var matches = songs.Take(3).Select(s => (s.Id, s.Name)).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeSong, locale);
            }

            songs = new List<BaseItem> { topMatch };
        }

        BaseItem song = songs[0];
        InsertAfterCurrent(session, song.Id);

        // If nothing is currently playing, start playback
        if (session.FullNowPlayingItem == null)
        {
            session.FullNowPlayingItem = song;
            string itemId = song.Id.ToString();
            return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, song, user);
        }

        Logger.LogInformation("PlayNext: {SongName} queued to play next", song.Name);
        return ResponseBuilder.Tell(ResponseStrings.Get("PlayNextConfirmed", locale, song.Name));
    }

    /// <summary>
    /// Insert an item right after the currently playing track in the queue.
    /// If nothing is playing, adds to the front.
    /// </summary>
    private static void InsertAfterCurrent(SessionInfo session, Guid itemId)
    {
        var queue = new List<QueueItem>(session.NowPlayingQueue);
        var newEntry = new QueueItem { Id = itemId };

        if (session.FullNowPlayingItem == null)
        {
            queue.Insert(0, newEntry);
            session.NowPlayingQueue = queue;
            return;
        }

        for (int i = 0; i < queue.Count; i++)
        {
            if (queue[i].Id == session.FullNowPlayingItem.Id)
            {
                queue.Insert(i + 1, newEntry);
                session.NowPlayingQueue = queue;
                return;
            }
        }

        // Current item not found in queue, insert at front
        queue.Insert(0, newEntry);
        session.NowPlayingQueue = queue;
    }
}

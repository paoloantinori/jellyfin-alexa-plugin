using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;

/// <summary>
/// Adds a named song to a named playlist ("add the song S to the playlist X").
/// The song is resolved with the bounded PlaySong pattern (server-side SearchTerm
/// query + exact/prefix pick), NOT the generic fuzzy helper: the Audio catalog is
/// too large for a full fuzzy scan (thousands of tracks would exceed Alexa's ~8s
/// window; the ban is documented on PlaySongIntentHandler). Gated on the song
/// index warm-up like every other cold-DB handler (JF-419).
/// </summary>
public class AddSongToPlaylistIntentHandler : PlaylistEditHandlerBase
{
    private readonly ISongNgramIndex? _songIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddSongToPlaylistIntentHandler"/> class.
    /// </summary>
    /// <param name="playlistManager">Instance of the <see cref="IPlaylistManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="songIndex">The song n-gram index (warming gate marker; optional for tests).</param>
    public AddSongToPlaylistIntentHandler(
        IPlaylistManager playlistManager,
        ISessionManager sessionManager,
        PluginConfiguration config,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory,
        ISongNgramIndex? songIndex = null)
        : base(playlistManager, sessionManager, config, userManager, libraryManager, loggerFactory)
    {
        _songIndex = songIndex;
    }

    /// <summary>Gets the intent name for this handler.</summary>
    protected override string IntentName => IntentNames.AddSongToPlaylist;

    /// <inheritdoc/>
    public async override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        var intentRequest = (IntentRequest)request;
        string? songName = GetSlotValue(intentRequest, IntentNames.Slots.Song);
        string? playlistName = GetSlotValue(intentRequest, IntentNames.Slots.PlaylistTarget);
        if (songName == null || playlistName == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchPlaylistName", locale));
        }

        // Music gate AFTER the slot prompt (BaseHandler.IfMediaTypeDisabled contract).
        SkillResponse? musicDisabled = IfMediaTypeDisabled(c => c.MusicEnabled, request);
        if (musicDisabled != null)
        {
            return musicDisabled;
        }

        // Cold-start gate (JF-419): the SearchTerm query below hits the database.
        GuardIndexReady(_songIndex);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, user.Id, locale);
        if (userError != null)
        {
            return userError;
        }

        MediaBrowser.Controller.Playlists.Playlist? playlist = FindPlaylist(playlistName, jellyfinUser!.Id);
        if (playlist == null)
        {
            Logger.LogDebug("AddSongToPlaylist: playlist '{Playlist}' not found", playlistName);
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPlaylist", locale, playlistName));
        }

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = songName,
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(query, user, _libraryManager);
        var candidates = await RetryAsync(
            () => _libraryManager.GetItemList(query),
            "AddSongToPlaylist search",
            cancellationToken).ConfigureAwait(false);
        BaseItem? match = candidates.FirstOrDefault(s => string.Equals(s.Name, songName, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => s.Name.StartsWith(songName, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByName", locale, songName));
        }

        await AddItemToPlaylistAsync(playlist.Id, new[] { match.Id }, jellyfinUser.Id).ConfigureAwait(false);
        Logger.LogInformation("AddSongToPlaylist: added {ItemName} to {PlaylistName}", match.Name, playlist.Name);
        return ResponseBuilder.Tell(ResponseStrings.Get("AddedToPlaylist", locale, match.Name, playlist.Name));
    }
}

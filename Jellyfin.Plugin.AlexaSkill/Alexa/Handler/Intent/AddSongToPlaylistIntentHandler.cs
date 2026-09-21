using System;
using System.Collections.Generic;
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

        // JF-550: an open elicit traps the next utterance into the slot, so a bare
        // cancel word must end the flow instead of searching for a song named "stop".
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "AddSongToPlaylist") is { } elicitCancel)
        {
            return elicitCancel;
        }

        string? songName = GetSlotValue(intentRequest, IntentNames.Slots.SongQuery);
        (string? rawPlaylistName, string? playlistName) = ReadPlaylistSlot(intentRequest, IntentNames.Slots.PlaylistTarget, locale);

        // JF-614 review: the song-only SearchQuery samples capture the WHOLE tail,
        // so "aggiungi la canzone rapsodia alla playlist rock" arrives as
        // song_query="rapsodia alla playlist rock". Split the playlist clause out
        // handler-side (the model cannot express both slots in one sample); when
        // the explicit playlist slot is also filled, it wins over the hint.
        if (songName != null && SplitPlaylistClause(songName, locale) is { } split)
        {
            Logger.LogDebug("AddSongToPlaylist: split playlist clause '{Hint}' out of the song query", split.Playlist);
            songName = split.Song;
            if (rawPlaylistName == null)
            {
                rawPlaylistName = split.Playlist;
                playlistName = Util.PlaylistNameNormalizer.NormalizePlaylistName(split.Playlist, locale);
            }
        }

        if (songName == null)
        {
            return BuildElicitSlotResponse(
                IntentNames.AddSongToPlaylist,
                IntentNames.Slots.SongQuery,
                Util.ElicitSlots.For(IntentNames.AddSongToPlaylist),
                ResponseStrings.Get("SpecifySongForPlaylistNoPlaylist", locale),
                slotValues: new Dictionary<string, string?> { [IntentNames.Slots.SongQuery] = null, [IntentNames.Slots.PlaylistTarget] = rawPlaylistName });
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

        // JF-614 review: resolve the song BEFORE the playlist elicit - a
        // mistyped title must fail fast, not waste the playlist turn first.
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = songName,
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true)
        };
        // Carrier-noun tolerance (live profile-nlu 2026-09-19: "aggiungi la canzone
        // {song}" fills song_query with "canzone rapsodia" - the noun rides into the slot
        // value in every locale's noun-carrying sample). The pick below therefore
        // also accepts a candidate the query merely ENDS with, which strips any
        // leading noun generically, no per-locale table (the JF-381 containment-band
        // shape, >=3 chars so a trailing article cannot match alone).
        ApplyLibraryFilter(query, user, _libraryManager);
        var candidates = await RetryAsync(
            () => _libraryManager.GetItemList(query),
            "AddSongToPlaylist search",
            cancellationToken).ConfigureAwait(false);
        BaseItem? match = candidates.FirstOrDefault(s => string.Equals(s.Name, songName, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => s.Name.StartsWith(songName, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => songName.EndsWith(s.Name, StringComparison.OrdinalIgnoreCase)
                && s.Name.Length >= 3);
        if (match == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByName", locale, songName));
        }

        if (playlistName == null)
        {
            // JF-601/JF-614: the song is resolved; the playlist is the second
            // dialog turn. The elicit echoes the resolved song back in the
            // updatedIntent so the round-trip cannot wipe it.
            return BuildElicitSlotResponse(
                IntentNames.AddSongToPlaylist,
                IntentNames.Slots.PlaylistTarget,
                Util.ElicitSlots.For(IntentNames.AddSongToPlaylist),
                ResponseStrings.Get("SpecifyPlaylistName", locale),
                slotValues: new Dictionary<string, string?> { [IntentNames.Slots.SongQuery] = songName, [IntentNames.Slots.PlaylistTarget] = null });
        }

        MediaBrowser.Controller.Playlists.Playlist? playlist = FindPlaylist(rawPlaylistName!, playlistName, jellyfinUser!.Id);
        if (playlist == null)
        {
            Logger.LogDebug("AddSongToPlaylist: playlist '{Playlist}' not found", playlistName);
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPlaylist", locale, playlistName));
        }

        await AddItemToPlaylistAsync(playlist.Id, new[] { match.Id }, jellyfinUser.Id).ConfigureAwait(false);
        Logger.LogInformation("AddSongToPlaylist: added {ItemName} to {PlaylistName}", match.Name, playlist.Name);
        return ResponseBuilder.Tell(ResponseStrings.Get("AddedToPlaylist", locale, match.Name, playlist.Name));
    }

    /// <summary>
    /// The playlist-clause markers the greedy song_query capture can carry,
    /// per language prefix (JF-614 review: the song-only samples capture the
    /// whole tail, so "X alla playlist Y" arrives in one slot).
    /// </summary>
    private static readonly Dictionary<string, string[]> PlaylistClauseMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["it"] = new[] { " alla playlist ", " nella playlist " },
        ["en"] = new[] { " to the playlist ", " in the playlist " },
        ["de"] = new[] { " zur playlist " },
        ["es"] = new[] { " a la lista " },
        ["fr"] = new[] { " à la liste de lecture ", " a la liste de lecture " },
        ["pt"] = new[] { " à playlist ", " a playlist " },
        ["nl"] = new[] { " aan de afspeellijst " },
    };

    /// <summary>
    /// Splits a playlist clause out of a greedy song_query capture. Returns null
    /// when the query carries no clause.
    /// </summary>
    /// <param name="songQuery">The raw song_query slot value.</param>
    /// <param name="locale">The request locale.</param>
    /// <returns>The song and playlist parts, or null.</returns>
    private static (string Song, string Playlist)? SplitPlaylistClause(string songQuery, string locale)
    {
        string prefix = locale.Split('-')[0].ToLowerInvariant();
        if (!PlaylistClauseMarkers.TryGetValue(prefix, out string[]? markers))
        {
            return null;
        }

        foreach (string marker in markers)
        {
            int index = songQuery.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                string song = songQuery[..index].Trim();
                string playlist = songQuery[(index + marker.Length)..].Trim();
                if (song.Length > 0 && playlist.Length > 0)
                {
                    return (song, playlist);
                }
            }
        }

        return null;
    }
}

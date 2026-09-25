using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;

/// <summary>
/// Shared plumbing for the voice playlist-editing intents (JF-325): resolves the
/// linked Jellyfin user, finds one of their visible playlists by spoken name
/// (exact, then prefix, then substring; playlist counts are user-small so the
/// tiered match is enough), and resolves "this" (the currently playing track)
/// from the AudioPlayer token first because <c>FullNowPlayingItem</c> is cleared
/// before some requests arrive (CLAUDE.md resume rule).
/// </summary>
public abstract class PlaylistEditHandlerBase : BaseHandler
{
    private protected readonly IPlaylistManager _playlistManager;
    private protected readonly IUserManager _userManager;
    private protected readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistEditHandlerBase"/> class.
    /// </summary>
    /// <param name="playlistManager">Instance of the <see cref="IPlaylistManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    protected PlaylistEditHandlerBase(
        IPlaylistManager playlistManager,
        ISessionManager sessionManager,
        PluginConfiguration config,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
        _playlistManager = playlistManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <summary>Gets the intent name for this handler.</summary>
    protected abstract string IntentName { get; }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The edit family's missing-playlist-name answer. Kept a Tell DELIBERATELY
    /// (JF-601 AC#3, the ONE rationale home): the string is a retry imperative
    /// ("Please try again"), not a question, so the user re-issues the full command
    /// one-shot; the dead-mic detector exempts this shape by design and an elicit
    /// here would need the intent registered in dialog.intents in all 17 models
    /// for one word of gain (AddSong's question-shaped prompts DO elicit).
    /// </summary>
    /// <param name="locale">The request locale.</param>
    /// <returns>The session-ending retry Tell.</returns>
    protected static SkillResponse SpecifyPlaylistNameTell(string locale)
        => ResponseBuilder.Tell(ResponseStrings.Get("SpecifyPlaylistName", locale));

    /// <summary>
    /// Finds a playlist visible to the user by spoken name: exact (case-insensitive),
    /// then prefix, then substring. The RAW spoken name runs every tier FIRST (a
    /// playlist genuinely named "Named Sessions" or "Chiamata Sei", created via the
    /// Jellyfin web UI or an .m3u import through the same server-wide playlist
    /// manager, keeps its match); the carrier-stripped form runs only on a miss,
    /// absorbing the "chiamata X" slot-fill leak (JF-602; the JF-469 fallback-only
    /// contract the album strip already follows).
    /// </summary>
    /// <param name="rawName">The raw spoken playlist name.</param>
    /// <param name="strippedName">The carrier-stripped name the caller already holds (may equal or be null).</param>
    /// <param name="jellyfinUserId">The linked Jellyfin user id.</param>
    /// <returns>The matched playlist, or null.</returns>
    protected Playlist? FindPlaylist(string rawName, string? strippedName, Guid jellyfinUserId)
    {
        var playlists = _playlistManager.GetPlaylists(jellyfinUserId).ToList();

        Playlist? Exact(string candidate) =>
            playlists.FirstOrDefault(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase));
        Playlist? Prefix(string candidate) =>
            playlists.FirstOrDefault(p => p.Name.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        Playlist? Substring(string candidate) =>
            playlists.FirstOrDefault(p => candidate.Contains(p.Name, StringComparison.OrdinalIgnoreCase)
                && p.Name.Length >= 3);

        // Tier INTERLEAVING (review round on JF-602): the loose substring tier on
        // the raw carrier-prefixed value must not preempt the stripped name's
        // exact tier (a "chiamata road trip" fill with playlists "road" and
        // "road trip" in the library must resolve to "road trip").
        bool hasStripped = strippedName != null
            && !string.Equals(strippedName, rawName, StringComparison.OrdinalIgnoreCase);
        Playlist? Stripped(Func<string, Playlist?> tier) => hasStripped ? tier(strippedName!) : null;

        return Exact(rawName)
            ?? Prefix(rawName)
            ?? Stripped(Exact)
            ?? Stripped(Prefix)
            ?? Substring(rawName)
            ?? Stripped(Substring);
    }

    /// <summary>
    /// Exact-name playlist lookup for the CREATE path's duplicate check (JF-615):
    /// raw first, stripped only when it differs (the hasStripped guard). Deliberately
    /// NOT the tiered <see cref="FindPlaylist"/>: a new name that merely CONTAINS an
    /// existing one ("prova echo quattro" vs "prova echo") must create, not be
    /// refused as a duplicate.
    /// </summary>
    /// <param name="rawName">The raw spoken playlist name.</param>
    /// <param name="strippedName">The carrier-stripped name the caller holds.</param>
    /// <param name="jellyfinUserId">The linked Jellyfin user id.</param>
    /// <returns>The exactly-matching playlist, or null.</returns>
    protected Playlist? FindExactPlaylist(string rawName, string? strippedName, Guid jellyfinUserId)
    {
        var playlists = _playlistManager.GetPlaylists(jellyfinUserId).ToList();
        Playlist? match = playlists.FirstOrDefault(p => string.Equals(p.Name, rawName, StringComparison.OrdinalIgnoreCase));
        if (match == null
            && strippedName != null
            && !string.Equals(strippedName, rawName, StringComparison.OrdinalIgnoreCase))
        {
            match = playlists.FirstOrDefault(p => string.Equals(p.Name, strippedName, StringComparison.OrdinalIgnoreCase));
        }

        return match;
    }

    /// <summary>
    /// Resolves the currently playing item through the ONE shared resolver
    /// (<see cref="PlaybackLaunchBuilder.ResolveCurrentPlayingItem"/>, JF-626).
    /// The one local fact: this family deliberately passes NO queue manager
    /// (null disables the resolver's device-ledger arms), keeping its
    /// token+session-only semantics.
    /// </summary>
    /// <param name="context">The Alexa request context.</param>
    /// <param name="session">The Jellyfin session.</param>
    /// <returns>The library item, or null when nothing is resolvable.</returns>
    protected BaseItem? ResolveCurrentItem(Context? context, SessionInfo? session)
        => Launch.ResolveCurrentPlayingItem(context, session, _libraryManager, queueManager: null);

    /// <summary>
    /// Adds an item to a playlist across the two Jellyfin API lines: 12.0 added a
    /// nullable insert-index parameter before the userId (JF-307 dual-target).
    /// </summary>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="itemIds">The items to add.</param>
    /// <param name="userId">The acting Jellyfin user id.</param>
    /// <returns>Completion.</returns>
    protected Task AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection<Guid> itemIds, Guid userId)
    {
#if JELLYFIN_12
        return _playlistManager.AddItemToPlaylistAsync(playlistId, itemIds, null, userId);
#else
        return _playlistManager.AddItemToPlaylistAsync(playlistId, itemIds, userId);
#endif
    }
}

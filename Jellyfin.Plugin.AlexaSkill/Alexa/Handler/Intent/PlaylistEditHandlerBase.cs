using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
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
    /// Finds a playlist visible to the user by spoken name: exact (case-insensitive),
    /// then prefix, then substring. Returns null when nothing matches.
    /// </summary>
    /// <param name="name">The spoken playlist name.</param>
    /// <param name="jellyfinUserId">The linked Jellyfin user id.</param>
    /// <returns>The matched playlist, or null.</returns>
    protected Playlist? FindPlaylist(string name, Guid jellyfinUserId)
    {
        var playlists = _playlistManager.GetPlaylists(jellyfinUserId).ToList();
        return playlists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? playlists.FirstOrDefault(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? playlists.FirstOrDefault(p => name.Contains(p.Name, StringComparison.OrdinalIgnoreCase)
                && p.Name.Length >= 3);
    }

    /// <summary>
    /// Resolves the currently playing item: the AudioPlayer token (survives
    /// PlaybackStopped clearing the session item) first, session now-playing second.
    /// </summary>
    /// <param name="context">The Alexa request context.</param>
    /// <param name="session">The Jellyfin session.</param>
    /// <returns>The library item, or null when nothing is resolvable.</returns>
    protected BaseItem? ResolveCurrentItem(Context? context, SessionInfo? session)
    {
        if (context?.AudioPlayer?.Token != null
            && Guid.TryParse(context.AudioPlayer.Token, out Guid tokenId))
        {
            BaseItem? tokenItem = _libraryManager.GetItemById(tokenId);
            if (tokenItem != null)
            {
                return tokenItem;
            }
        }

        if (session?.NowPlayingItem != null)
        {
            return _libraryManager.GetItemById(session.NowPlayingItem.Id);
        }

        return null;
    }

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

    /// <summary>
    /// Extracts a slot's raw spoken value (whitespace-normalized), null when absent
    /// (the IsNullOrWhiteSpace guard rule, CLAUDE.md anti-pattern #7).
    /// </summary>
    /// <param name="request">The intent request.</param>
    /// <param name="slotName">The slot name.</param>
    /// <returns>The trimmed slot value, or null.</returns>
    protected static string? GetSlotValue(IntentRequest request, string slotName)
    {
        if (request.Intent.Slots == null || !request.Intent.Slots.TryGetValue(slotName, out Slot? slot))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(slot.Value) ? null : slot.Value.Trim();
    }
}

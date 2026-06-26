using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Resolves the playable audio tracks of a Jellyfin playlist.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin playlists are NOT folders: their members are linked children stored in the
/// <c>Playlists</c> join table, and <see cref="Playlist"/>'s own
/// <c>LoadChildren()</c> deliberately returns an empty list. Querying
/// <c>ILibraryManager</c> with <c>ParentId = playlistId</c> therefore always returns zero
/// items, so the plugin reported every playlist as empty even though the web UI could play it.
/// </para>
/// <para>
/// This resolver reads members through <see cref="Playlist.GetManageableItems"/> — the same
/// API used by Jellyfin's own <c>PlaylistsController.GetPlaylistItems</c>. See GitHub issue #10.
/// </para>
/// </remarks>
internal static class PlaylistTrackResolver
{
    /// <summary>
    /// Returns the audio tracks of <paramref name="playlist"/> that are visible to
    /// <paramref name="user"/>, in stable playlist order. Non-audio and unresolved
    /// members are excluded. Callers that paginate MUST iterate the result of this
    /// method in order (e.g. <c>.Take(n)</c> then <c>.Skip(n)</c>) so the initial page
    /// and continuation batches line up.
    /// </summary>
    /// <param name="playlist">The playlist whose tracks to resolve.</param>
    /// <param name="user">The Jellyfin user, for visibility filtering. Pass null to skip visibility filtering.</param>
    /// <returns>Visible audio <see cref="BaseItem"/>s in playlist order. Never null.</returns>
    public static IReadOnlyList<BaseItem> GetAudioTracks(Playlist? playlist, User? user)
    {
        if (playlist is null)
        {
            return Array.Empty<BaseItem>();
        }

        return FilterAudioTracks(playlist.GetManageableItems(), user);
    }

    /// <summary>
    /// Pure filter over the raw manageable-items list: keeps audio items that are visible
    /// to the user, drops unresolved entries, and preserves playlist order. Split out so the
    /// filtering contract is unit-testable independently of the DB-backed
    /// <see cref="Playlist.GetManageableItems"/> call.
    /// </summary>
    /// <param name="manageableItems">The raw (linked child, resolved item) tuples from the playlist.</param>
    /// <param name="user">The Jellyfin user, for visibility filtering. Pass null to skip visibility filtering.</param>
    /// <returns>Visible audio <see cref="BaseItem"/>s in input order. Never null.</returns>
    internal static IReadOnlyList<BaseItem> FilterAudioTracks(
        IEnumerable<Tuple<LinkedChild, BaseItem>> manageableItems,
        User? user)
    {
        return manageableItems
            .Where(t => t?.Item2 is not null)
            .Where(t => t.Item2.MediaType == MediaType.Audio)
            .Where(t => user is null || t.Item2.IsVisible(user))
            .Select(t => t.Item2)
            .ToList();
    }
}

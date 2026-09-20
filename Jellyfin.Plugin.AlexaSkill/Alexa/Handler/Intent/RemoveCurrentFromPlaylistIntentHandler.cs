using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
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
/// Removes the currently playing track from a named playlist ("remove this from
/// the playlist X"). Jellyfin's remove takes the media item GUIDs in "N" format
/// and matches them against the playlist's linked children (verified against the
/// PlaylistManager source).
/// </summary>
public class RemoveCurrentFromPlaylistIntentHandler : PlaylistEditHandlerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoveCurrentFromPlaylistIntentHandler"/> class.
    /// </summary>
    /// <param name="playlistManager">Instance of the <see cref="IPlaylistManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public RemoveCurrentFromPlaylistIntentHandler(
        IPlaylistManager playlistManager,
        ISessionManager sessionManager,
        PluginConfiguration config,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory)
        : base(playlistManager, sessionManager, config, userManager, libraryManager, loggerFactory)
    {
    }

    /// <summary>Gets the intent name for this handler.</summary>
    protected override string IntentName => IntentNames.RemoveCurrentFromPlaylist;

    /// <inheritdoc/>
    public async override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        var intentRequest = (IntentRequest)request;
        string? playlistName = GetPlaylistSlotValue(intentRequest, IntentNames.Slots.Playlist, locale);
        if (playlistName == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("SpecifyPlaylistName", locale));
        }

        BaseItem? current = ResolveCurrentItem(context, session);
        if (current == null)
        {
            Logger.LogDebug("RemoveCurrentFromPlaylist: no resolvable current item");
            return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale));
        }

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, user.Id, locale);
        if (userError != null)
        {
            return userError;
        }

        MediaBrowser.Controller.Playlists.Playlist? playlist = FindPlaylist(playlistName, jellyfinUser!.Id);
        if (playlist == null)
        {
            Logger.LogDebug("RemoveCurrentFromPlaylist: playlist '{Playlist}' not found", playlistName);
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPlaylist", locale, playlistName));
        }

        // Awaited (review JF-325): same silent-failure rule as the add path.
        await _playlistManager.RemoveItemFromPlaylistAsync(
            playlist.Id.ToString("N", CultureInfo.InvariantCulture),
            new[] { current.Id.ToString("N", CultureInfo.InvariantCulture) }).ConfigureAwait(false);
        Logger.LogInformation("RemoveCurrentFromPlaylist: removed {ItemName} from {PlaylistName}", current.Name, playlist.Name);
        return ResponseBuilder.Tell(ResponseStrings.Get("RemovedFromPlaylist", locale, current.Name, playlist.Name));
    }
}

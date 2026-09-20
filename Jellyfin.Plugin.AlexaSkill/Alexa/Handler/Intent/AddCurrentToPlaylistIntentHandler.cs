using System;
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
/// Adds the currently playing track to a named playlist ("add this to the
/// playlist X"). The track comes from the AudioPlayer token (or the session's
/// now-playing item as fallback); duplicates are ignored server-side
/// (Jellyfin dedupes against the playlist's linked children).
/// </summary>
public class AddCurrentToPlaylistIntentHandler : PlaylistEditHandlerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AddCurrentToPlaylistIntentHandler"/> class.
    /// </summary>
    /// <param name="playlistManager">Instance of the <see cref="IPlaylistManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public AddCurrentToPlaylistIntentHandler(
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
    protected override string IntentName => IntentNames.AddCurrentToPlaylist;

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
            Logger.LogDebug("AddCurrentToPlaylist: no resolvable current item");
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
            Logger.LogDebug("AddCurrentToPlaylist: playlist '{Playlist}' not found", playlistName);
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPlaylist", locale, playlistName));
        }

        // Awaited (review JF-325): the confirmation must not outrun the write; a
        // local playlist write is milliseconds, well inside the Alexa window.
        await AddItemToPlaylistAsync(playlist.Id, new[] { current.Id }, jellyfinUser.Id).ConfigureAwait(false);
        Logger.LogInformation("AddCurrentToPlaylist: added {ItemName} to {PlaylistName}", current.Name, playlist.Name);
        return ResponseBuilder.Tell(ResponseStrings.Get("AddedToPlaylist", locale, current.Name, playlist.Name));
    }
}

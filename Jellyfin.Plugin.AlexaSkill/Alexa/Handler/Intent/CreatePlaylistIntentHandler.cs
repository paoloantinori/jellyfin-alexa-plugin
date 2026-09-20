using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;

/// <summary>
/// Creates a new playlist by name ("create a playlist called X"). Fails with a
/// dedicated message when the Jellyfin library has no Playlists folder (the
/// manager throws in that case) and when a playlist with the same name already
/// exists (the server would otherwise silently create "name1").
/// </summary>
public class CreatePlaylistIntentHandler : PlaylistEditHandlerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CreatePlaylistIntentHandler"/> class.
    /// </summary>
    /// <param name="playlistManager">Instance of the <see cref="IPlaylistManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public CreatePlaylistIntentHandler(
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
    protected override string IntentName => IntentNames.CreatePlaylist;

    /// <inheritdoc/>
    public async override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        var intentRequest = (IntentRequest)request;
        (string? rawPlaylistName, string? playlistName) = ReadPlaylistSlot(intentRequest, IntentNames.Slots.Playlist, locale);
        if (playlistName == null)
        {
            return SpecifyPlaylistNameTell(locale);
        }

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, user.Id, locale);
        if (userError != null)
        {
            return userError;
        }

        MediaBrowser.Controller.Playlists.Playlist? existing = FindPlaylist(rawPlaylistName!, playlistName, jellyfinUser!.Id);
        if (existing != null)
        {
            // Speak the REAL playlist's name: the raw tier may have matched a
            // web-UI playlist the stripped echo would misname (review round JF-602).
            return ResponseBuilder.Tell(ResponseStrings.Get("PlaylistAlreadyExists", locale, existing.Name));
        }

        try
        {
            PlaylistCreationResult result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlistName,
                UserId = jellyfinUser.Id,
                MediaType = Jellyfin.Data.Enums.MediaType.Audio,
            }).ConfigureAwait(false);
            Logger.LogInformation("CreatePlaylist: created '{PlaylistName}' ({Id})", playlistName, result.Id);
            return ResponseBuilder.Tell(ResponseStrings.Get("PlaylistCreated", locale, playlistName));
        }
        catch (ArgumentException ex)
        {
            // Thrown when the library has no Playlists folder (manager source).
            Logger.LogWarning(ex, "CreatePlaylist: no playlists folder for user");
            return ResponseBuilder.Tell(ResponseStrings.Get("PlaylistCreateFailed", locale));
        }
    }
}

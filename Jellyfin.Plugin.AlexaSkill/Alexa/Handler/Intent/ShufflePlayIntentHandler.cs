using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for ShufflePlayIntent — start a playlist already shuffled (first track random).
/// Shares the playlist-play flow with <see cref="PlayPlaylistIntentHandler"/> via
/// <see cref="BaseHandler.BuildPlaylistPlayResponseAsync"/> (shuffle enabled).
/// </summary>
public class ShufflePlayIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShufflePlayIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public ShufflePlayIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.ShufflePlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// Play a playlist by its name, shuffled. Delegates the shared playlist-play flow to
    /// <see cref="BaseHandler.BuildPlaylistPlayResponseAsync"/> with shuffle enabled.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Play directive of the shuffled playlist.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        string? playlistName = intentRequest.Intent.Slots?.TryGetValue("playlist", out var playlistSlot) == true ? playlistSlot.Value : null;
        return BuildPlaylistPlayResponseAsync(
            _libraryManager, _userManager, _queueManager,
            playlistName ?? string.Empty, context, user, session, locale,
            shuffle: true, rng: null, cancellationToken);
    }
}

using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for UnmarkFavoriteIntent intents.
/// </summary>
public class UnmarkFavoriteIntentHandler : FavoriteToggleIntentHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnmarkFavoriteIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    /// <param name="queueManager">The device queue manager owning the last-played ledger the shared resolver reads; null disables the ledger arms (no Plugin.Instance fallback).</param>
    public UnmarkFavoriteIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, userDataManager, userManager, libraryManager, loggerFactory, queueManager)
    {
    }

    /// <inheritdoc/>
    protected override string IntentName => IntentNames.UnmarkFavorite;

    /// <inheritdoc/>
    protected override bool FavoriteValue => false;

    /// <inheritdoc/>
    protected override string ResponseKey => "RemovedFromFavorites";
}

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
    protected override string IntentName => IntentNames.UnmarkFavorite;
    protected override bool FavoriteValue => false;
    protected override string ResponseKey => "RemovedFromFavorites";

    public UnmarkFavoriteIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, userDataManager, userManager, libraryManager, loggerFactory)
    {
    }
}

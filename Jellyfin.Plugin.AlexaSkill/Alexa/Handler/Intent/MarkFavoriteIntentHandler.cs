using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for MarkFavoriteIntent intents.
/// </summary>
public class MarkFavoriteIntentHandler : FavoriteToggleIntentHandler
{
    protected override string IntentName => IntentNames.MarkFavorite;
    protected override bool FavoriteValue => true;
    protected override string ResponseKey => "AddedToFavorites";

    public MarkFavoriteIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, userDataManager, userManager, libraryManager, loggerFactory)
    {
    }
}

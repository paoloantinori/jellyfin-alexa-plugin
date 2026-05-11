using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>
    /// Initializes a new instance of the <see cref="MarkFavoriteIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public MarkFavoriteIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, userDataManager, userManager, libraryManager, loggerFactory)
    {
    }

    /// <summary>
    /// Gets the intent name for this handler.
    /// </summary>
    protected override string IntentName => IntentNames.MarkFavorite;

    /// <summary>
    /// Gets a value indicating whether the favorite flag should be set or cleared.
    /// </summary>
    protected override bool FavoriteValue => true;

    /// <summary>
    /// Gets the response key for the result message.
    /// </summary>
    protected override string ResponseKey => "AddedToFavorites";
}

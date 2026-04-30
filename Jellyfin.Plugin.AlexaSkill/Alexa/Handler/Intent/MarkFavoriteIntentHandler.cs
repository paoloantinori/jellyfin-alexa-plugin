using System.Threading;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for MarkFavoriteIntent intents.
/// </summary>
public class MarkFavoriteIntentHandler : BaseHandler
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    public MarkFavoriteIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "MarkFavoriteIntent", System.StringComparison.Ordinal);
    }

    public override SkillResponse Handle(Request request, Context context, Entities.User user, SessionInfo session)
    {
        string locale = GetLocale(request);
        BaseItemDto? item = session.NowPlayingItem;
        if (item == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        var jellyfinUser = _userManager.GetUserById(user.Id);
        var baseItem = _libraryManager.GetItemById(item.Id);
        if (baseItem == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        var data = _userDataManager.GetUserData(jellyfinUser, baseItem);
        data.IsFavorite = true;
        _userDataManager.SaveUserData(jellyfinUser, baseItem, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);

        return ResponseBuilder.Tell(ResponseStrings.Get("AddedToFavorites", locale));
    }
}
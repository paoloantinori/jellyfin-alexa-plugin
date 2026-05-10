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
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Base handler for toggling favorite status on the currently playing item.
/// </summary>
public abstract class FavoriteToggleIntentHandler : BaseHandler
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    protected abstract string IntentName { get; }
    protected abstract bool FavoriteValue { get; }
    protected abstract string ResponseKey { get; }

    protected FavoriteToggleIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentName, StringComparison.Ordinal);
    }

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        BaseItemDto? item = session.NowPlayingItem;
        if (item == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, user.Id, locale);
        if (userError != null)
        {
            return Task.FromResult<SkillResponse>(userError);
        }

        var baseItem = _libraryManager.GetItemById(item.Id);
        if (baseItem == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        var data = _userDataManager.GetUserData(jellyfinUser, baseItem);
        if (data == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        data.IsFavorite = FavoriteValue;
        _userDataManager.SaveUserData(jellyfinUser, baseItem, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);

        return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get(ResponseKey, locale)));
    }
}

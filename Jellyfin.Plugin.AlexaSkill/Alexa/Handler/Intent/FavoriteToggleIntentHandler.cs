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

    /// <summary>
    /// Initializes a new instance of the <see cref="FavoriteToggleIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
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

    /// <summary>
    /// Gets the intent name for this handler.
    /// </summary>
    protected abstract string IntentName { get; }

    /// <summary>
    /// Gets a value indicating whether the favorite flag should be set or cleared.
    /// </summary>
    protected abstract bool FavoriteValue { get; }

    /// <summary>
    /// Gets the response key for the result message.
    /// </summary>
    protected abstract string ResponseKey { get; }

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

        Jellyfin.Database.Implementations.Entities.User resolvedUser = jellyfinUser!;

        var baseItem = _libraryManager.GetItemById(item.Id);
        if (baseItem == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        var data = _userDataManager.GetUserData(resolvedUser, baseItem);
        if (data == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        data.IsFavorite = FavoriteValue;
        _userDataManager.SaveUserData(resolvedUser, baseItem, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);

        return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get(ResponseKey, locale)));
    }
}

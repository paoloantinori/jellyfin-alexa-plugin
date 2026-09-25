using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Base handler for toggling favorite status on the currently playing item. The
/// current item comes from the ONE shared resolver
/// (<see cref="PlaybackLaunchBuilder.ResolveCurrentPlayingItem"/>, JF-626/JF-629):
/// the composite-safe AudioPlayer token, the session item, and the device ledger's
/// displacement arbitration, so "metti questa nei preferiti" resolves the same
/// item "ripeti" and "valuta" do after PlaybackStopped cleared the session DTO
/// or a VideoApp launch displaced the audio.
/// </summary>
public abstract class FavoriteToggleIntentHandler : BaseHandler
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FavoriteToggleIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">The device queue manager owning the last-played ledger the shared resolver reads; null disables the ledger arms (no <c>Plugin.Instance</c> fallback).</param>
    protected FavoriteToggleIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _queueManager = queueManager;
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

        // The ONE current-item resolver (JF-629, the RateItem sibling): the
        // codec-safe AudioPlayer token, the session item, and the device-ledger
        // displacement arbitration whose predicate and rationale live in
        // PlaybackLaunchBuilder.ResolveCurrentPlayingItem.
        BaseItem? item = Launch.ResolveCurrentPlayingItem(context, session, _libraryManager, _queueManager, IntentName);
        if (item == null)
        {
            Logger.LogDebug("FavoriteToggle ({IntentName}): no resolvable current item, returning MediaNotFound", IntentName);
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        Logger.LogDebug("FavoriteToggle ({IntentName}): item={ItemName}, favorite={FavoriteValue}", IntentName, item.Name, FavoriteValue);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, user.Id, locale);
        if (userError != null)
        {
            return Task.FromResult<SkillResponse>(userError);
        }

        Jellyfin.Database.Implementations.Entities.User resolvedUser = jellyfinUser!;

        var data = _userDataManager.GetUserData(resolvedUser, item);
        if (data == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        data.IsFavorite = FavoriteValue;
        _userDataManager.SaveUserData(resolvedUser, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);

        return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get(ResponseKey, locale)));
    }
}

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
/// Handler for RateItemIntent: writes a 1-5 star user rating on the item the
/// device is playing. The current item comes from the ONE shared resolver
/// (<see cref="PlaybackLaunchBuilder.ResolveCurrentPlayingItem"/>, JF-626):
/// the composite-safe AudioPlayer token, the session item, and the device
/// ledger's JF-562/JF-568/JF-625 displacement arbitration.
/// </summary>
public class RateItemIntentHandler : BaseHandler
{
    /// <summary>
    /// Jellyfin stores UserItemData.Rating as a bare double with no server-side
    /// scale (Emby.Server.Implementations UserDataManager copies the DTO value
    /// verbatim) and NO first-party client writes the field today (jellyfin-web's
    /// rating button is favorite-only; verified master/v10.4.0/v10.0.0): the 0-10
    /// mapping is THIS PLUGIN'S OWN convention, chosen to align with the decimal
    /// 0-10 scale CommunityRating lives on and the legacy Emby 5-star UI's x2
    /// mapping. Spoken stars are written as <c>stars * 2.0</c> ("3 stars" becomes
    /// 6.0), which keeps our own rating-first sorting (ResumeMath) monotonic. If a
    /// first-party client ever writes the field on a different scale, the two
    /// conventions interleave silently in rating-sorted ordering; nothing flags it.
    /// </summary>
    private const double RatingPerStar = 2.0;

    private const int MaxStars = 5;

    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateItemIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">The device queue manager owning the last-played ledger the shared resolver reads; null disables the ledger arms (no <c>Plugin.Instance</c> fallback).</param>
    public RateItemIntentHandler(
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

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.RateItem, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override Task<SkillResponse> HandleAsync(
        Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        // While a Dialog.ElicitSlot is open, Alexa captures the next utterance
        // INTO the elicited slot, so a bare "stop" arrives as star_rating="stop"
        // with dialogState IN_PROGRESS. That is a cancel, not a rating (the
        // shared JF-550 elicitation-trap hatch).
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "RateItem") is { } elicitCancel)
        {
            return Task.FromResult<SkillResponse>(elicitCancel);
        }

        // ItalianNumberWords parses digit strings (every locale's AMAZON.NUMBER
        // delivery) AND the Italian number words the it-IT model's ItalianNumber
        // slot delivers ("cinque", not "5"; the GoToChapter precedent).
        if (intentRequest.Intent.Slots == null
            || !intentRequest.Intent.Slots.TryGetValue(IntentNames.Slots.StarRating, out Slot? ratingSlot)
            || !ItalianNumberWords.TryParse(ratingSlot.Value, out int stars))
        {
            // JF-549 shape: an empty-slot branch ASKS with the mic open, never a
            // Tell-question (the answer "five" can only route back through the
            // elicit; the intent is registered in dialog.intents for it).
            Logger.LogDebug("RateItem: no parsable {Slot} slot value", IntentNames.Slots.StarRating);
            return Task.FromResult<SkillResponse>(BuildDialogElicitResponse(
                "DidNotCatchStarRating", locale, IntentNames.Slots.StarRating,
                IntentNames.RateItem, Util.ElicitSlots.For(IntentNames.RateItem)));
        }

        if (stars < 1 || stars > MaxStars)
        {
            Logger.LogDebug("RateItem: rating {Stars} out of the 1-{Max} range", stars, MaxStars);
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("RatingOutOfRange", locale)));
        }

        BaseItem? item = Launch.ResolveCurrentPlayingItem(context, session, _libraryManager, _queueManager, "RateItem");
        if (item == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("RatingNoItem", locale)));
        }

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, user.Id, locale);
        if (userError != null)
        {
            return Task.FromResult<SkillResponse>(userError);
        }

        Jellyfin.Database.Implementations.Entities.User resolvedUser = jellyfinUser!;

        var data = _userDataManager.GetUserData(resolvedUser, item);
        if (data == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("RatingNoItem", locale)));
        }

        double storedRating = stars * RatingPerStar;
        data.Rating = storedRating;
        _userDataManager.SaveUserData(resolvedUser, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);

        Logger.LogInformation("RateItem: rated '{ItemName}' ({ItemId}) {Stars}/{Max} stars (stored {Stored})", item.Name, item.Id, stars, MaxStars, storedRating);
        return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("RatingSet", locale, stars, item.Name)));
    }
}

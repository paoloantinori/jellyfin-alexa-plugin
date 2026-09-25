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
/// device is playing. The current item is resolved with RepeatIntentHandler's
/// JF-562/JF-568 token-vs-ledger arbitration (see
/// <see cref="ResolveCurrentItem"/> for the order and the deltas from Repeat:
/// the composite-safe token codec, and the plugin-instance ledger fallback).
/// </summary>
public class RateItemIntentHandler : BaseHandler
{
    /// <summary>
    /// Jellyfin stores UserItemData.Rating as a bare double with no server-side
    /// scale (Emby.Server.Implementations UserDataManager copies the DTO value
    /// verbatim), so the range is client convention: the classic 5-star UI maps
    /// one star onto 2.0 of the 0-10 decimal scale CommunityRating also uses
    /// (half-star steps of 1.0). Spoken stars are therefore written as
    /// <c>stars * 2.0</c> ("3 stars" becomes 6.0), which keeps our own
    /// rating-first sorting (ResumeMath) monotonic and matches the scale other
    /// Jellyfin tooling assumes for the field.
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
    /// <param name="queueManager">The device queue manager owning the last-played ledger; null falls back to <c>Plugin.Instance</c>'s.</param>
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

        BaseItem? item = ResolveCurrentItem(context, session);
        if (item == null)
        {
            Logger.LogDebug("RateItem: no resolvable current item");
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

    /// <summary>
    /// Resolves the item to rate, mirroring RepeatIntentHandler's JF-562/JF-568
    /// arbitration: the AudioPlayer token first (via the composite-safe codec,
    /// so a sleep-timer token still resolves), the session's full now-playing
    /// item second (free; no re-resolve by id), and the device last-played
    /// ledger last (the only record a VideoApp launch leaves, required for the
    /// seek-mode video route). When the token and the ledger disagree, a
    /// VideoApp-routed video-kind or audiobook ledger entry means the video
    /// displaced the audio: the stale music token must lose (rating it would
    /// write the rating on the wrong item), while an audio-routed disagreement
    /// is the ordinary queue-advance shape where the fresher token wins. The
    /// cheap displacement guards run BEFORE the ledger resolve (the
    /// ResolvePlayingMedium doctrine), so the ordinary music path pays one
    /// item resolve, not two.
    /// </summary>
    /// <param name="context">The Alexa request context.</param>
    /// <param name="session">The Jellyfin session.</param>
    /// <returns>The item to rate, or null when nothing is resolvable.</returns>
    private BaseItem? ResolveCurrentItem(Context? context, SessionInfo? session)
    {
        string? deviceId = context?.System?.Device?.DeviceID;
        DeviceQueueManager? ledgerManager = deviceId != null
            ? _queueManager ?? Plugin.Instance?.DeviceQueueManager
            : null;
        string? lastPlayedId = deviceId != null ? ledgerManager?.GetLastPlayedItemId(deviceId) : null;
        DeviceQueueManager.LaunchRoute? recordedRoute = deviceId != null ? ledgerManager?.GetLastPlayedLaunchRoute(deviceId) : null;
        string? token = context?.AudioPlayer?.Token;

        BaseItem? ResolveId(string? id) =>
            !string.IsNullOrEmpty(id) && Guid.TryParse(id, out Guid guid)
                ? _libraryManager.GetItemById(guid)
                : null;

        // The three data-in-hand guards before the ledger item's kind can
        // matter: when any fails (the modal music shape: audio-routed ledger,
        // resolvable token) the resolve is skipped, mirroring ResolvePlayingMedium.
        bool displacementPossible =
            !string.IsNullOrEmpty(token)
            && !string.Equals(token, lastPlayedId, StringComparison.Ordinal)
            && recordedRoute != DeviceQueueManager.LaunchRoute.Audio;

        BaseItem? ledgerItem = displacementPossible ? ResolveId(lastPlayedId) : null;
        bool videoDisplacedAudio =
            ledgerItem != null
            && (PlaybackLaunchBuilder.IsVideoAppLaunchItem(ledgerItem)
                || AudiobookItems.IsAudioBook(ledgerItem));

        if (videoDisplacedAudio)
        {
            Logger.LogDebug(
                "RateItem: AudioPlayer token displaced by the VideoApp launch of '{ItemName}'; the video is current",
                ledgerItem!.Name);
            return ledgerItem;
        }

        // The shared codec, not raw Guid.TryParse: tokens can be composite
        // ("{guid}|sleep:{ticks}", JF-447), and a raw parse would silently
        // decline to the session fallback while a sleep timer is armed.
        if (Playback.StreamTokenCodec.TryGetItemId(token, out Guid tokenId))
        {
            BaseItem? tokenItem = _libraryManager.GetItemById(tokenId);
            if (tokenItem != null)
            {
                return tokenItem;
            }
        }

        // A full BaseItem the session already holds; no re-resolve by id.
        if (session?.FullNowPlayingItem is { } sessionItem)
        {
            return sessionItem;
        }

        return ledgerItem ?? ResolveId(lastPlayedId);
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for FollowMeIntent: pulls the active queue from another device onto the current one.
///
/// Alexa custom skills can only send AudioPlayer directives to the device that sent
/// the request. Therefore "follow me" works by having the user speak to the TARGET device:
///   1. Source device is playing music (tracked by DeviceQueueManager)
///   2. User walks to another room, speaks to that Echo: "ask jellyfin to follow me"
///   3. This handler finds the most recently active queue from any OTHER device
///   4. Replays the queue's current item on the current device, carrying the source
///      device's playback position when one was recorded (JF-375): the queue's live
///      pointer first, the per-item store second, both fail-closed through the shared
///      runtime clamp. Nothing recorded = offset 0 and the plain announcement; a
///      carried offset announces the resume wording. Still true: the SOURCE device
///      is not stopped (platform wall, see README).
/// </summary>
public class FollowMeIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FollowMeIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for cross-device state lookup.</param>
    public FollowMeIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.FollowMe, StringComparison.Ordinal);
    }

    /// <summary>
    /// Find what was playing on another device and resume it on the current device.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response resuming playback or an error message.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        string currentDeviceId = context.System!.Device!.DeviceID;

        if (_queueManager == null)
        {
            Logger.LogWarning("FollowMeIntent: DeviceQueueManager not available");
            return ResponseBuilder.Tell(ResponseStrings.Get("FollowMeNothingPlaying", locale));
        }

        // Find all active queues from OTHER devices, sorted by most recently modified
        var otherQueues = _queueManager.GetAllActiveQueues(excludeDeviceId: currentDeviceId)
            .OrderByDescending(q => q.Queue.LastModifiedUtc)
            .ToList();

        if (otherQueues.Count == 0)
        {
            Logger.LogInformation("FollowMeIntent: no active queue found on other devices for current device {DeviceId}", currentDeviceId);
            return ResponseBuilder.Tell(ResponseStrings.Get("FollowMeNothingPlaying", locale));
        }

        // Pick the most recently active queue
        var (sourceDeviceId, sourceQueue) = otherQueues[0];
        string currentItemId = sourceQueue.ItemIds[sourceQueue.CurrentIndex];

        Logger.LogInformation(
            "FollowMeIntent: transferring queue from device {SourceDevice} to {TargetDevice}, item={ItemId}, index={Index}",
            sourceDeviceId, currentDeviceId, currentItemId, sourceQueue.CurrentIndex);

        // Look up the item for metadata (title, art).
        // GetItemById returns BaseItem? so we call it directly rather than through RetryAsync.
        MediaBrowser.Controller.Entities.BaseItem? item = await RetryAsync(
            () => _libraryManager.GetItemById(Guid.Parse(currentItemId))!,
            "FollowMeGetItem",
            cancellationToken).ConfigureAwait(false);

        if (item == null)
        {
            Logger.LogWarning("FollowMeIntent: could not find item {ItemId}", currentItemId);
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        // Transfer the queue to the current device
        _queueManager.SetQueue(
            currentDeviceId,
            sourceQueue.ItemIds,
            sourceQueue.CurrentIndex,
            sourceQueue.RepeatMode,
            sourceQueue.PlaybackOrder);

        // Update Jellyfin session queue
        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        session.FullNowPlayingItem = item;

        // JF-375: carry the source device's playback position. Two signals, freshest
        // first: the queue's live per-device pointer (CurrentItemId/CurrentPositionTicks),
        // then the durable per-item store. Both are plugin-owned, so the
        // FullNowPlayingItem clearing documented in CLAUDE.md cannot take this offset
        // away. Read BEFORE the source Clear below. The pointer comparison is
        // GUID-parsed, not string-compared: production writers store the dashed
        // format while the store keys on "N" (review C1 - a string compare silently
        // favored one writer and killed the freshest signal).
        long carryTicks = 0;
        if (Guid.TryParse(currentItemId, out Guid normalizedItem))
        {
            carryTicks = Guid.TryParse(sourceQueue.CurrentItemId, out Guid pointerItem)
                && pointerItem == normalizedItem
                && sourceQueue.CurrentPositionTicks > 0
                    ? sourceQueue.CurrentPositionTicks
                    : _queueManager.GetStoredPositionTicks(sourceDeviceId, currentItemId) ?? 0;
        }

        // The ONE fail-closed runtime clamp (JF-565/JF-586): a position that cannot
        // be proven within the item runtime drops to 0 - and logs, so the spoken
        // "right where you left it" can never cover a stale offset.
        carryTicks = Launch.ClampResumeTicksToRuntime(item, carryTicks, "FollowMe carry");

        int offsetMs = (int)(carryTicks / 10_000);
        Logger.LogInformation(
            "FollowMeIntent: carry position {OffsetMs}ms from device {SourceDevice} for item {ItemId}",
            offsetMs, sourceDeviceId, currentItemId);

        string streamUrl = Launch.GetStreamUrl(currentItemId, user);
        string title = item.Name ?? ResponseStrings.Get("UnknownMedia", locale);

        SkillResponse response = Launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            streamUrl,
            currentItemId,
            item,
            user,
            context,
            offsetInMilliseconds: offsetMs);

        // Replace the default speech with the follow-me announcement. Two wordings,
        // honest in both directions: the carried-position phrase only when an offset
        // actually applies (nothing stored = the transfer genuinely starts at 0).
        response.Response.OutputSpeech = offsetMs > 0
            ? SpeechBuilder.BuildOutputSpeech("FollowMeSuccessResumeSsml", "FollowMeSuccessResume", locale, title)
            : SpeechBuilder.BuildOutputSpeech("FollowMeSuccessSsml", "FollowMeSuccess", locale, title);

        // Clear the source device's queue so it doesn't keep appearing as "active"
        _queueManager.Clear(sourceDeviceId);

        return response;
    }
}

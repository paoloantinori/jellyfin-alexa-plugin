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

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for FollowMeIntent: resumes playback from another device onto the current one.
///
/// Alexa custom skills can only send AudioPlayer directives to the device that sent
/// the request. Therefore "follow me" works by having the user speak to the TARGET device:
///   1. Source device is playing music (tracked by DeviceQueueManager)
///   2. User walks to another room, speaks to that Echo: "ask jellyfin to follow me"
///   3. This handler finds the most recently active queue from any OTHER device
///   4. Resumes playback of that queue on the current device at the stored offset
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

        // Build the audio response (offset 0 since we don't track per-device playback position
        // through DeviceQueueManager — the offset comes from the AudioPlayer context which is
        // only available on the source device, not here)
        string streamUrl = GetStreamUrl(currentItemId, user);
        string title = item.Name ?? ResponseStrings.Get("UnknownMedia", locale);

        SkillResponse response = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            streamUrl,
            currentItemId,
            item,
            user,
            context);

        // Replace the default speech with the follow-me announcement
        string? ssml = GetSsml("FollowMeSuccessSsml", locale, EscapeXml(title));
        response.Response.OutputSpeech = ssml != null
            ? new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" }
            : new PlainTextOutputSpeech { Text = ResponseStrings.Get("FollowMeSuccess", locale, title) };

        // Clear the source device's queue so it doesn't keep appearing as "active"
        _queueManager.Clear(sourceDeviceId);

        return response;
    }
}

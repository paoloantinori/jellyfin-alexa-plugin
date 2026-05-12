using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Request interceptor that checks whether the requesting Alexa device supports
/// the <c>AudioPlayer</c> interface. When the device lacks audio playback capability
/// (e.g. an Echo Show with no speaker, a third-party device without AudioPlayer),
/// the interceptor short-circuits with a localized error message before any Jellyfin
/// API calls are made, avoiding wasted backend round-trips.
/// </summary>
/// <remarks>
/// Registered early in the request pipeline (after <see cref="CircuitBreakerInterceptor"/>)
/// so that unsupported-device requests fail fast.
/// </remarks>
public class AudioDeviceCapabilityInterceptor : IRequestInterceptor
{
    private const string AudioPlayerInterfaceKey = "AudioPlayer";
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioDeviceCapabilityInterceptor"/> class.
    /// </summary>
    /// <param name="logger">Logger for capability check events.</param>
    public AudioDeviceCapabilityInterceptor(ILogger<AudioDeviceCapabilityInterceptor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        // Only gate intent requests that would trigger audio/video playback.
        // System events (PlaybackNearlyFinished, PlaybackFailed, etc.) and
        // LaunchRequest are either audio-player-initiated already or don't
        // produce playback, so there's no point blocking them.
        if (context.SkillRequest is not IntentRequest)
        {
            return Task.FromResult(true);
        }

        bool supportsAudioPlayer = context.AlexaContext?.System?.Device?.SupportedInterfaces?
            .ContainsKey(AudioPlayerInterfaceKey) == true;

        if (supportsAudioPlayer)
        {
            return Task.FromResult(true);
        }

        string locale = BaseHandler.GetLocalePublic(context.SkillRequest);
        _logger.LogWarning(
            "Device {DeviceId} does not support AudioPlayer — short-circuiting intent {Intent}",
            context.AlexaContext?.System?.Device?.DeviceID ?? "unknown",
            context.IntentName);

        context.Response = ResponseBuilder.Tell(ResponseStrings.Get("AudioPlayerNotSupported", locale));
        return Task.FromResult(false);
    }
}

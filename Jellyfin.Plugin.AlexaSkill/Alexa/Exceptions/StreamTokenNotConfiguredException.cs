using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;

/// <summary>
/// Thrown by a launch builder's delivery decision when the stream URL about to be
/// delivered targets a plugin token-gated endpoint while
/// <see cref="Configuration.PluginConfiguration.StreamTokenSecret"/> is empty (JF-687):
/// such a URL is dead at birth (the JF-309 route gate 503s every fetch before reading
/// any token), so the launch refuses instead of handing the device a URL it fails on
/// opaquely. The request pipeline's single <see cref="SkillRefusalException"/> catch
/// translates it (see that base for the shared policy) into the localized
/// <c>StreamTokenNotConfigured</c> Tell, exactly like
/// <see cref="SkillWarmingUpException"/>; because the refusal propagates as an
/// exception, no handler tail (announce overwrites, now-playing state, persists) runs
/// on a refused launch (JF-699 item 1). Deliberately carries NO url: minted stream
/// URLs embed the signed token, and an exception type that carries one invites a log
/// path that leaks it.
/// </summary>
public sealed class StreamTokenNotConfiguredException : SkillRefusalException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StreamTokenNotConfiguredException"/> class.
    /// </summary>
    public StreamTokenNotConfiguredException()
        : base(
            "Launch refused: the stream token secret is not configured, so a token-gated stream URL would be rejected by the route gate (JF-687)",
            "StreamTokenNotConfigured",
            LogLevel.Error)
    {
    }
}

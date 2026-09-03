#nullable enable
using Alexa.NET.Request;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Shared extraction of the per-device key from an Alexa request context (JF-447
/// hygiene: the null-conditional chain was copied inline across the event handlers and
/// tests). The empty-string fallback keeps every unattributed request on ONE shared
/// slot, the same keying the per-device state stores already use.
/// </summary>
internal static class AlexaContextExtensions
{
    /// <summary>
    /// Extracts the device ID from the request context.
    /// </summary>
    /// <param name="context">The Alexa request context.</param>
    /// <returns>The device ID, or an empty string when the request carries none.</returns>
    internal static string GetDeviceId(this Context context)
        => context.System?.Device?.DeviceID ?? string.Empty;
}

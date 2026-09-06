using Alexa.NET.Request;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Interface;

/// <summary>
/// Request-side VideoApp capability detection (JF-505). Mirrors
/// <see cref="Jellyfin.Plugin.AlexaSkill.Alexa.Apl.AplHelper.DeviceSupportsApl"/> for the VideoApp interface: the
/// platform reports the interfaces a device supports in
/// <c>context.System.Device.SupportedInterfaces</c>, and a VideoApp.Launch sent to a
/// device without the VideoApp interface (an Echo Dot) is rejected with an audible
/// directive error (device evidence 2026-09-06, corr=58826ad3 and corr=f0240020).
/// </summary>
public static class VideoAppCapabilities
{
    /// <summary>
    /// The <c>SupportedInterfaces</c> key screen-capable devices (Echo Show, Fire TV)
    /// report for the VideoApp interface. Note this is the REQUEST-context key, not the
    /// <c>VIDEO_APP</c> manifest interface name <see cref="VideoAppInterface"/> maps to.
    /// </summary>
    public const string VideoAppInterfaceKey = "VideoApp";

    /// <summary>
    /// Check whether the requesting device supports the VideoApp interface. Fails OPEN
    /// when the context carries no <c>SupportedInterfaces</c> map at all: a device that
    /// REPORTS its interfaces without VideoApp is gated, while absent capability data
    /// keeps the pre-gate launch behavior instead of refusing video on a guess.
    /// </summary>
    /// <param name="context">The Alexa request context, or null.</param>
    /// <returns>True when the device can receive a VideoApp.Launch directive.</returns>
    public static bool DeviceSupportsVideoApp(Context? context)
    {
        var interfaces = context?.System?.Device?.SupportedInterfaces;
        return interfaces == null || interfaces.ContainsKey(VideoAppInterfaceKey);
    }
}

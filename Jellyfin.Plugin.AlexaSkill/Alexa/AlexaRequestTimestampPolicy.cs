using System;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// JF-311 replay protection: Amazon requires rejecting Alexa requests whose
/// timestamp falls outside a 150-second window. Extracted as pure logic so the
/// threshold and skew tolerance are unit-testable independent of signature verification.
/// </summary>
public static class AlexaRequestTimestampPolicy
{
    /// <summary>The maximum age (and future skew) tolerance, in seconds, per Amazon's request model.</summary>
    public const double WindowSeconds = 150;

    /// <summary>True when <paramref name="timestamp"/> is within the window of <paramref name="now"/>.</summary>
    public static bool IsWithinWindow(DateTime timestamp, DateTime now)
    {
        double ageSeconds = Math.Abs((now - timestamp).TotalSeconds);
        return ageSeconds <= WindowSeconds;
    }
}

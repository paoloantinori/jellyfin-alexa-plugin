using System;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-311 unit tests for <see cref="AlexaRequestTimestampPolicy"/>.
/// Pure logic — no Plugin.Instance or DI dependency.
/// </summary>
public class AlexaRequestTimestampPolicyTests
{
    [Fact]
    public void FreshTimestamp_IsWithinWindow()
    {
        var now = DateTime.UtcNow;
        Assert.True(AlexaRequestTimestampPolicy.IsWithinWindow(now, now));
    }

    [Fact]
    public void TenMinutesAgo_IsOutsideWindow()
    {
        var now = DateTime.UtcNow;
        var ts = now.AddMinutes(-10);
        Assert.False(AlexaRequestTimestampPolicy.IsWithinWindow(ts, now));
    }

    [Fact]
    public void TenMinutesFuture_IsOutsideWindow()
    {
        var now = DateTime.UtcNow;
        var ts = now.AddMinutes(10);
        Assert.False(AlexaRequestTimestampPolicy.IsWithinWindow(ts, now));
    }

    [Fact]
    public void ExactlyAtWindowBoundary_IsWithinWindow()
    {
        var now = DateTime.UtcNow;
        var ts = now.AddSeconds(-AlexaRequestTimestampPolicy.WindowSeconds);
        Assert.True(AlexaRequestTimestampPolicy.IsWithinWindow(ts, now));
    }

    [Fact]
    public void OneSecondPastWindowBoundary_IsOutsideWindow()
    {
        var now = DateTime.UtcNow;
        var ts = now.AddSeconds(-(AlexaRequestTimestampPolicy.WindowSeconds + 1));
        Assert.False(AlexaRequestTimestampPolicy.IsWithinWindow(ts, now));
    }
}

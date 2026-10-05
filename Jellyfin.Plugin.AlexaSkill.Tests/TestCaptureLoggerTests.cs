using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests;

/// <summary>
/// The JF-760 non-vacuity pin for
/// <see cref="TestCaptureLogger.CreateCaptureLoggerFactory(List{(LogLevel, string)})"/>:
/// both load-bearing wirings (the capture-provider registration and the Trace
/// floor, rationale on the helper) are pinned by this one test, so the
/// migrated log-asserting consumers inherit the protection permanently.
/// </summary>
public class TestCaptureLoggerTests
{
    [Fact]
    public void CreateCaptureLoggerFactory_CapturesDownToTraceLevel()
    {
        var records = new List<(LogLevel Level, string Message)>();
        using var factory = TestCaptureLogger.CreateCaptureLoggerFactory(records);

        ILogger logger = factory.CreateLogger(nameof(TestCaptureLoggerTests));
        logger.LogTrace("the trace floor must hold");
        logger.LogDebug("debug records must arrive");
        logger.LogInformation("information records must arrive");

        Assert.Equal(3, records.Count);
        Assert.Contains(records, r => r.Level == LogLevel.Trace && r.Message.Contains("trace floor", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Level == LogLevel.Debug && r.Message.Contains("debug records", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Level == LogLevel.Information && r.Message.Contains("information records", StringComparison.Ordinal));
    }
}

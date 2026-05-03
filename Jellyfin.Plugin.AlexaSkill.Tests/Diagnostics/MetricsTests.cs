using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Diagnostics;

public class MetricsTests
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    // --- RequestCounters extended metrics ---

    [Fact]
    public void RecordResponseTime_CreatesNewEntry()
    {
        var counters = new RequestCounters();
        counters.RecordResponseTime("PlaySongIntent", 150.5);

        IReadOnlyDictionary<string, IntentMetricsSnapshot> metrics = counters.GetIntentMetrics();
        Assert.Single(metrics);
        Assert.True(metrics.ContainsKey("PlaySongIntent"));

        IntentMetricsSnapshot snapshot = metrics["PlaySongIntent"];
        Assert.Equal(1, snapshot.Count);
        Assert.Equal(150.5, snapshot.TotalMs);
        Assert.Equal(150.5, snapshot.AverageMs);
        Assert.Equal(150.5, snapshot.MinMs);
        Assert.Equal(150.5, snapshot.MaxMs);
    }

    [Fact]
    public void RecordResponseTime_AccumulatesMultipleEntries()
    {
        var counters = new RequestCounters();
        counters.RecordResponseTime("PlaySongIntent", 100);
        counters.RecordResponseTime("PlaySongIntent", 200);
        counters.RecordResponseTime("PlaySongIntent", 300);

        IntentMetricsSnapshot snapshot = counters.GetIntentMetrics()["PlaySongIntent"];
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(600, snapshot.TotalMs);
        Assert.Equal(200, snapshot.AverageMs);
        Assert.Equal(100, snapshot.MinMs);
        Assert.Equal(300, snapshot.MaxMs);
    }

    [Fact]
    public void RecordResponseTime_TracksSeparateIntents()
    {
        var counters = new RequestCounters();
        counters.RecordResponseTime("PlaySongIntent", 100);
        counters.RecordResponseTime("PauseIntent", 50);

        IReadOnlyDictionary<string, IntentMetricsSnapshot> metrics = counters.GetIntentMetrics();
        Assert.Equal(2, metrics.Count);
        Assert.Equal(1, metrics["PlaySongIntent"].Count);
        Assert.Equal(1, metrics["PauseIntent"].Count);
    }

    [Fact]
    public void IncrementIntentError_TracksErrors()
    {
        var counters = new RequestCounters();
        counters.RecordResponseTime("PlaySongIntent", 100);
        counters.IncrementIntentError("PlaySongIntent");

        IntentMetricsSnapshot snapshot = counters.GetIntentMetrics()["PlaySongIntent"];
        Assert.Equal(1, snapshot.Count);
        Assert.Equal(1, snapshot.ErrorCount);
        Assert.NotNull(snapshot.LastErrorAt);
    }

    [Fact]
    public void IncrementIntentError_NoPriorMetrics_CreatesEntry()
    {
        var counters = new RequestCounters();
        counters.IncrementIntentError("UnknownIntent");

        IntentMetricsSnapshot snapshot = counters.GetIntentMetrics()["UnknownIntent"];
        Assert.Equal(0, snapshot.Count);
        Assert.Equal(1, snapshot.ErrorCount);
        Assert.NotNull(snapshot.LastErrorAt);
    }

    [Fact]
    public void GetIntentMetrics_EmptyWhenNoData()
    {
        var counters = new RequestCounters();
        Assert.Empty(counters.GetIntentMetrics());
    }

    [Fact]
    public void Uptime_IsPositive()
    {
        var counters = new RequestCounters();
        Assert.True(counters.Uptime > TimeSpan.Zero);
    }

    [Fact]
    public async Task IntentMetrics_AreThreadSafe()
    {
        var counters = new RequestCounters();
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                counters.RecordResponseTime("ConcurrentIntent", 10);
                counters.IncrementIntentError("ConcurrentIntent");
            }));
        }

        await Task.WhenAll(tasks);

        IntentMetricsSnapshot snapshot = counters.GetIntentMetrics()["ConcurrentIntent"];
        Assert.Equal(100, snapshot.Count);
        Assert.Equal(100, snapshot.ErrorCount);
        Assert.Equal(1000, snapshot.TotalMs);
    }

    // --- MetricsResponseInterceptor ---

    [Fact]
    public async Task MetricsInterceptor_RecordsResponseTime()
    {
        var counters = new RequestCounters();
        var interceptor = new MetricsResponseInterceptor(counters, _loggerFactory.CreateLogger<MetricsResponseInterceptor>());

        var skillRequest = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            Locale = "en-US"
        };

        var context = new RequestContext(
            skillRequest,
            Unit.TestHelpers.CreateTestContext(),
            null,
            new Mock<BaseHandler>(
                new Mock<ISessionManager>().Object,
                new PluginConfiguration(),
                _loggerFactory).Object);

        context.StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-50);

        await interceptor.ProcessAsync(context, CancellationToken.None);

        IntentMetricsSnapshot snapshot = counters.GetIntentMetrics()["PlaySongIntent"];
        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.AverageMs >= 45); // Approximate
    }

    [Fact]
    public async Task MetricsInterceptor_UsesRequestType_WhenNotIntent()
    {
        var counters = new RequestCounters();
        var interceptor = new MetricsResponseInterceptor(counters, _loggerFactory.CreateLogger<MetricsResponseInterceptor>());

        var skillRequest = new LaunchRequest { Locale = "en-US" };

        var context = new RequestContext(
            skillRequest,
            Unit.TestHelpers.CreateTestContext(),
            null,
            new Mock<BaseHandler>(
                new Mock<ISessionManager>().Object,
                new PluginConfiguration(),
                _loggerFactory).Object);

        context.StartedAt = DateTimeOffset.UtcNow;

        await interceptor.ProcessAsync(context, CancellationToken.None);

        // LaunchRequest.Type is null when not deserialized from JSON, so RequestContext.RequestType returns "unknown"
        IntentMetricsSnapshot snapshot = counters.GetIntentMetrics()["unknown"];
        Assert.Equal(1, snapshot.Count);
    }
}

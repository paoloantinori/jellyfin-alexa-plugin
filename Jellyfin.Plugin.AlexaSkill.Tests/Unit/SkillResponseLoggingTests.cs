using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alexa.NET;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class SkillResponseLoggingTests : PluginTestBase
{
    // --- RecordResponseSize bucket tests ---

    [Fact]
    public void RecordResponseSize_SmallBucket_Under1KB()
    {
        var counters = new RequestCounters();
        counters.RecordResponseSize(512);

        Assert.Equal(1, counters.ResponseSizeSmall);
        Assert.Equal(0, counters.ResponseSizeMedium);
        Assert.Equal(0, counters.ResponseSizeLarge);
    }

    [Fact]
    public void RecordResponseSize_SmallBucket_Exactly1023()
    {
        var counters = new RequestCounters();
        counters.RecordResponseSize(1023);

        Assert.Equal(1, counters.ResponseSizeSmall);
        Assert.Equal(0, counters.ResponseSizeMedium);
    }

    [Fact]
    public void RecordResponseSize_MediumBucket_Exactly1KB()
    {
        var counters = new RequestCounters();
        counters.RecordResponseSize(1024);

        Assert.Equal(0, counters.ResponseSizeSmall);
        Assert.Equal(1, counters.ResponseSizeMedium);
        Assert.Equal(0, counters.ResponseSizeLarge);
    }

    [Fact]
    public void RecordResponseSize_MediumBucket_Exactly10239()
    {
        var counters = new RequestCounters();
        counters.RecordResponseSize(10239);

        Assert.Equal(0, counters.ResponseSizeSmall);
        Assert.Equal(1, counters.ResponseSizeMedium);
        Assert.Equal(0, counters.ResponseSizeLarge);
    }

    [Fact]
    public void RecordResponseSize_LargeBucket_Exactly10KB()
    {
        var counters = new RequestCounters();
        counters.RecordResponseSize(10240);

        Assert.Equal(0, counters.ResponseSizeSmall);
        Assert.Equal(0, counters.ResponseSizeMedium);
        Assert.Equal(1, counters.ResponseSizeLarge);
    }

    [Fact]
    public void RecordResponseSize_LargeBucket_Over10KB()
    {
        var counters = new RequestCounters();
        counters.RecordResponseSize(50000);

        Assert.Equal(0, counters.ResponseSizeSmall);
        Assert.Equal(0, counters.ResponseSizeMedium);
        Assert.Equal(1, counters.ResponseSizeLarge);
    }

    [Fact]
    public void RecordResponseSize_MultipleCalls_Accumulates()
    {
        var counters = new RequestCounters();
        counters.RecordResponseSize(100);   // small
        counters.RecordResponseSize(500);   // small
        counters.RecordResponseSize(2048);  // medium
        counters.RecordResponseSize(20000); // large
        counters.RecordResponseSize(100);   // small

        Assert.Equal(3, counters.ResponseSizeSmall);
        Assert.Equal(1, counters.ResponseSizeMedium);
        Assert.Equal(1, counters.ResponseSizeLarge);
    }

    [Fact]
    public void RecordResponseSize_ConcurrentIncrements_Accurate()
    {
        var counters = new RequestCounters();
        const int iterations = 100;

        Parallel.For(0, iterations, i =>
        {
            // Distribute across buckets: 0-32 small, 33-66 medium, 67-99 large
            if (i < 33)
            {
                counters.RecordResponseSize(100);
            }
            else if (i < 67)
            {
                counters.RecordResponseSize(5000);
            }
            else
            {
                counters.RecordResponseSize(50000);
            }
        });

        Assert.Equal(33, counters.ResponseSizeSmall);
        Assert.Equal(34, counters.ResponseSizeMedium);
        Assert.Equal(33, counters.ResponseSizeLarge);
    }

    [Fact]
    public void RecordResponseSize_ZeroBytes_IsSmall()
    {
        var counters = new RequestCounters();
        counters.RecordResponseSize(0);

        Assert.Equal(1, counters.ResponseSizeSmall);
    }

    // --- Log level verification ---

    /// <summary>
    /// The production response-size line (AlexaSkillController.SkillResponseContent)
    /// must log at Debug, never Information: Jellyfin's default log level is
    /// Information, so an Information-level response line would land in every
    /// server's log on every skill response. Drives the REAL controller path (the
    /// empty-body early return is the cheapest route through SkillResponseContent,
    /// needing no signature, session, or user setup) and asserts the captured
    /// record's level. Until JF-786 this test built the capture but never asserted
    /// it (the vacuity predates JF-760's migration).
    /// </summary>
    [Fact]
    public async Task SkillResponseContent_LogsAtDebugLevel_NotInformation()
    {
        TestHelpers.EnsureRealPlugin();

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
        var counters = new RequestCounters();

        var controller = new AlexaSkillController(
            new Mock<IUserManager>().Object,
            new Mock<ISessionManager>().Object,
            loggerFactory,
            counters,
            // The pipeline argument is constructor-required plumbing only: this
            // route returns before ExecuteAsync is ever consulted.
            new RequestPipeline(
                Enumerable.Empty<IRequestInterceptor>(),
                Enumerable.Empty<IResponseInterceptor>(),
                loggerFactory.CreateLogger<RequestPipeline>()),
            Enumerable.Empty<BaseHandler>());

        // An empty request body takes the controller's early return, whose answer
        // still goes through SkillResponseContent; DefaultHttpContext supplies the
        // Request the endpoint reads directly.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        ActionResult result = await controller.HandleIntentRequest();

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);
        // The served body must carry the serialized response: a regression that
        // logs and counts but serves an empty (or whitespace-only) body would
        // otherwise stay green. IsNullOrWhiteSpace per the repo convention.
        Assert.False(string.IsNullOrWhiteSpace(content.Content), "the served body must be non-empty");

        // Pin the exercised route, not just its output shape: the controller's
        // catch paths also answer via SkillResponseContent with the same single
        // Debug line, so the asserts above alone stay green if the empty-body
        // branch ever reroutes to an error path. The early return's unique
        // Warning line is the discriminator.
        Assert.Contains(TestCaptureLogger.Snapshot(logRecords),
            r => r.Level == LogLevel.Warning && r.Message.Contains("Received empty request body", StringComparison.Ordinal));

        // The named contract: exactly one response-size line and it is Debug, so
        // none can be Information (Single plus the level assert enforce both
        // halves of the name; a second line at any level fails Single first).
        var responseLine = Assert.Single(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("Skill response:", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, responseLine.Level);

        // The same production site records the serialized size through the real
        // path (this was the test's only assert before JF-786, previously on a
        // hand-driven counter call instead of the controller).
        Assert.True(counters.ResponseSizeSmall + counters.ResponseSizeMedium + counters.ResponseSizeLarge >= 1,
            "SkillResponseContent should have recorded the serialized response size");
    }

    /// <summary>
    /// Verify that a typical empty SkillResponse falls in the small bucket.
    /// </summary>
    [Fact]
    public void EmptySkillResponse_FallsInSmallBucket()
    {
        var counters = new RequestCounters();
        var response = ResponseBuilder.Empty();
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(response);

        counters.RecordResponseSize(json.Length);

        Assert.Equal(1, counters.ResponseSizeSmall);
        Assert.True(json.Length < 1024,
            $"Expected empty response under 1KB but got {json.Length} bytes");
    }

    /// <summary>
    /// Verify that a SkillResponse with a Tell speech output falls in the small bucket.
    /// </summary>
    [Fact]
    public void TellResponse_FallsInSmallBucket()
    {
        var counters = new RequestCounters();
        var response = ResponseBuilder.Tell("Playing your music");
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(response);

        counters.RecordResponseSize(json.Length);

        // A simple Tell response should be well under 1KB
        Assert.True(json.Length < 1024,
            $"Expected Tell response under 1KB but got {json.Length} bytes");
        Assert.Equal(1, counters.ResponseSizeSmall);
    }
}

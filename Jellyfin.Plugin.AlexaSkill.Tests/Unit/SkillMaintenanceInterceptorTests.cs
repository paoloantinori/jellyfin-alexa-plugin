using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using AlexaSession = Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-846 pins for the maintenance-window transparency: the refcounted
/// <see cref="SkillMaintenanceScope"/> (open/close transitions, double-dispose,
/// Information window logging) and <see cref="SkillMaintenanceInterceptor"/>
/// (prefix while open, byte-identical while closed, directive-only and silent
/// stop shapes untouched, event requests skipped, refcount nesting). The scope
/// is process-global static state (hence the Plugin collection, which serializes
/// this class against the sync-service tests whose SyncUserLibraryAsync calls
/// open real windows): single-scope pins go through
/// <see cref="ProcessWhileOpenAsync"/> (whose using can never leak a scope),
/// and the refcount pin manages its scopes by hand in try/finally.
/// </summary>
[Collection("Plugin")]
public class SkillMaintenanceInterceptorTests
{
    private readonly SkillMaintenanceInterceptor _interceptor;

    public SkillMaintenanceInterceptorTests()
    {
        _interceptor = new SkillMaintenanceInterceptor(
            LoggerFactory.Create(b => b.AddDebug()).CreateLogger<SkillMaintenanceInterceptor>());
    }

    private static RequestContext CreateContext(
        SkillResponse? response,
        Request? request = null,
        string locale = "en-US")
    {
        request ??= new IntentRequest { Type = "IntentRequest" };
        request.Locale = locale;

        var handler = new Mock<BaseHandler>(
            Mock.Of<ISessionManager>(),
            new PluginConfiguration(),
            LoggerFactory.Create(b => { }));

        return new RequestContext(request, TestHelpers.CreateTestContext(), new AlexaSession { New = false }, handler.Object)
        {
            Response = response
        };
    }

    /// <summary>Runs the interceptor once with a maintenance window open, then closes it.</summary>
    private async Task ProcessWhileOpenAsync(RequestContext context)
    {
        using var scope = SkillMaintenanceScope.Open("test operation", NullLogger.Instance);
        await _interceptor.ProcessAsync(context, CancellationToken.None);
    }

    private static string EnPrefix => ResponseStrings.Get(SkillMaintenanceInterceptor.PrefixKey, "en-US");

    private static PlainTextOutputSpeech SpeechOf(SkillResponse response)
        => (PlainTextOutputSpeech)response.Response.OutputSpeech!;

    // =====================================================================
    // Scope pins
    // =====================================================================

    [Fact]
    public void Scope_Open_Close_TransitionsIsOpen()
    {
        Assert.False(SkillMaintenanceScope.IsOpen);
        var scope = SkillMaintenanceScope.Open("test operation", NullLogger.Instance);
        try
        {
            Assert.True(SkillMaintenanceScope.IsOpen);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.False(SkillMaintenanceScope.IsOpen);
    }

    [Fact]
    public void Scope_DoubleDispose_DoesNotOverCloseTheWindow()
    {
        var first = SkillMaintenanceScope.Open("first", NullLogger.Instance);
        var second = SkillMaintenanceScope.Open("second", NullLogger.Instance);
        try
        {
            Assert.True(SkillMaintenanceScope.IsOpen);

            // A double-disposed token must not decrement twice: the second
            // opener would see its window close early.
            first.Dispose();
            first.Dispose();
            Assert.True(SkillMaintenanceScope.IsOpen);
        }
        finally
        {
            second.Dispose();
        }

        Assert.False(SkillMaintenanceScope.IsOpen);
    }

    /// <summary>
    /// The triage correlation JF-846 asks for: window open/close land at
    /// Information carrying the operation name, so a user complaint can be
    /// matched against the maintenance window it fell inside.
    /// </summary>
    [Fact]
    public void Scope_OpenAndClose_LogAtInformationWithOperationName()
    {
        var records = new List<(LogLevel Level, string Message)>();
        using var factory = TestCaptureLogger.CreateCaptureLoggerFactory(records);

        var scope = SkillMaintenanceScope.Open("catalog sync (user test)", factory.CreateLogger("scope"));
        scope.Dispose();

        var snapshot = TestCaptureLogger.Snapshot(records);
        Assert.Contains(snapshot, r => r.Level == LogLevel.Information
            && r.Message.Contains("maintenance window opened", StringComparison.Ordinal)
            && r.Message.Contains("catalog sync (user test)", StringComparison.Ordinal));
        Assert.Contains(snapshot, r => r.Level == LogLevel.Information
            && r.Message.Contains("maintenance window closed", StringComparison.Ordinal)
            && r.Message.Contains("catalog sync (user test)", StringComparison.Ordinal));
    }

    // =====================================================================
    // Interceptor pins: prefix applied
    // =====================================================================

    [Fact]
    public async Task Open_Scope_PrefixesPlainTextSpeech()
    {
        var response = ResponseBuilder.Tell("Found it.");
        var ctx = CreateContext(response);

        await ProcessWhileOpenAsync(ctx);

        Assert.Equal($"{EnPrefix} Found it.", SpeechOf(response).Text);
    }

    [Fact]
    public async Task Open_Scope_PrefixesSsmlSpeechInsideSpeakTags()
    {
        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                OutputSpeech = new SsmlOutputSpeech { Ssml = "<speak>Found it.</speak>" }
            }
        };
        var ctx = CreateContext(response);

        await ProcessWhileOpenAsync(ctx);

        var ssml = (SsmlOutputSpeech)response.Response.OutputSpeech!;
        Assert.Equal($"<speak>{SpeechBuilder.EscapeXml(EnPrefix)} Found it.</speak>", ssml.Ssml);
    }

    /// <summary>
    /// The Ask shape (open session): the prefix lands on the OutputSpeech and
    /// the Reprompt stays byte-identical - the prefix informs once, it does not
    /// re-ask patience on the reprompt.
    /// </summary>
    [Fact]
    public async Task Open_Scope_PrefixesSpeech_LeavesRepromptUntouched()
    {
        var response = ResponseBuilder.Ask("Which one?", new Reprompt("The first or the second?"));
        string repromptBefore = ((PlainTextOutputSpeech)response.Response.Reprompt!.OutputSpeech!).Text;
        var ctx = CreateContext(response);

        await ProcessWhileOpenAsync(ctx);

        Assert.Equal($"{EnPrefix} Which one?", SpeechOf(response).Text);
        Assert.Equal(repromptBefore, ((PlainTextOutputSpeech)response.Response.Reprompt!.OutputSpeech!).Text);
    }

    [Fact]
    public async Task Open_Scope_UsesRequestLocale()
    {
        var response = ResponseBuilder.Tell("Trovato.");
        var ctx = CreateContext(response, locale: "it-IT");

        await ProcessWhileOpenAsync(ctx);

        Assert.StartsWith("Sto aggiornando la skill", SpeechOf(response).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<speak>Found it.</speak>", "<speak>PREFIX Found it.</speak>")]
    [InlineData("<speak>Found it.</speak>\n", "<speak>PREFIX Found it.</speak>\n")]
    [InlineData("Found it.", "<speak>PREFIX Found it.</speak>")]
    [InlineData("", "<speak>PREFIX </speak>")]
    public void PrefixIntoSsml_WrapsWhenNeeded(string input, string expected)
    {
        Assert.Equal(expected, SkillMaintenanceInterceptor.PrefixIntoSsml(input, "PREFIX"));
    }

    // =====================================================================
    // Interceptor pins: prefix withheld
    // =====================================================================

    [Fact]
    public async Task Closed_Scope_LeavesSpeechByteIdentical()
    {
        var response = ResponseBuilder.Tell("Found it.");
        string before = SpeechOf(response).Text;
        var ctx = CreateContext(response);

        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Equal(before, SpeechOf(response).Text);
    }

    /// <summary>
    /// Directive-only responses (the VideoApp launch shape) are never given
    /// speech: the VideoApp rules forbid extra fields on that response and the
    /// progressive announce owns the spoken part of that path.
    /// </summary>
    [Fact]
    public async Task Open_Scope_DirectiveOnlyVideoAppLaunch_StaysSpeechless()
    {
        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = null,
                Directives = new List<IDirective>
                {
                    new VideoAppLaunchDirective
                    {
                        VideoItem = new Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoItem { Source = "https://jellyfin.example/Videos/guid/stream?static=true" }
                    }
                }
            }
        };
        var ctx = CreateContext(response);

        await ProcessWhileOpenAsync(ctx);

        Assert.Null(response.Response.OutputSpeech);
        Assert.Single(response.Response.Directives!);
    }

    /// <summary>
    /// The docs-mandated silent stop shape (AudioPlayer.Stop directive, session
    /// ended, no speech) must stay silent while a window is open: stop answers
    /// are not a place to advertise maintenance.
    /// </summary>
    [Fact]
    public async Task Open_Scope_SilentStopShape_StaysSilent()
    {
        SkillResponse response = BaseHandler.BuildPauseResponse();
        var ctx = CreateContext(response);

        await ProcessWhileOpenAsync(ctx);

        Assert.Null(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    /// <summary>
    /// JF-507 defensive leg: AudioPlayer events / SessionEnded /
    /// System.ExceptionEncountered responses may not carry outputSpeech at all;
    /// even a response that (wrongly) carries speech on an event request gets
    /// no prefix here rather than more speech.
    /// </summary>
    [Fact]
    public async Task Open_Scope_EventRequest_NeverPrefixed()
    {
        var response = ResponseBuilder.Tell("should not be spoken");
        var ctx = CreateContext(response, request: new AudioPlayerRequest());

        await ProcessWhileOpenAsync(ctx);

        Assert.Equal("should not be spoken", SpeechOf(response).Text);
    }

    // =====================================================================
    // Refcount nesting: the window outlives one closer
    // =====================================================================

    [Fact]
    public async Task Refcount_TwoOpeners_PrefixStopsOnlyAfterBothClose()
    {
        var first = SkillMaintenanceScope.Open("catalog sync", NullLogger.Instance);
        var second = SkillMaintenanceScope.Open("custom-model rebuild", NullLogger.Instance);
        try
        {
            var ctx = CreateContext(ResponseBuilder.Tell("Found it."));
            await _interceptor.ProcessAsync(ctx, CancellationToken.None);
            Assert.Equal($"{EnPrefix} Found it.", SpeechOf(ctx.Response!).Text);

            // First opener done; the rebuild still holds the window open.
            first.Dispose();
            var ctxAfterFirstClose = CreateContext(ResponseBuilder.Tell("Found it."));
            await _interceptor.ProcessAsync(ctxAfterFirstClose, CancellationToken.None);
            Assert.Equal($"{EnPrefix} Found it.", SpeechOf(ctxAfterFirstClose.Response!).Text);

            // Last closer ends the window; the prefix stops.
            second.Dispose();
            var ctxAfterBoth = CreateContext(ResponseBuilder.Tell("Found it."));
            await _interceptor.ProcessAsync(ctxAfterBoth, CancellationToken.None);
            Assert.Equal("Found it.", SpeechOf(ctxAfterBoth.Response!).Text);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }
}

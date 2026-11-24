#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// The JF-931 deserialization seam: an error.type wire value unknown to Alexa.NET's
/// ErrorType enum must not kill the request at deserialization. The handler-reaching
/// tests feed raw bodies through the PRODUCTION seam
/// (<see cref="SkillRequestDeserializer.Deserialize"/>, the same call
/// AlexaSkillController and SimulatorController make), not a re-implementation; the two
/// naive-path tests pin the pre-JF-931 JsonConvert call shape the fix replaced.
/// Premise (verified against the pinned Alexa.NET 1.22.0 binary): every ErrorType
/// member carries an EnumMember SCREAMING_SNAKE wire name StringEnumConverter honors,
/// so the mapped vocabulary is wire names plus member names, and a value absent from
/// BOTH (Amazon keeps adding error.type values; the enum has grown 4 to 33 members)
/// threw "Requested value ... was not found" before JF-931.
/// </summary>
[Collection("Plugin")]
public class SkillRequestDeserializerTests : IDisposable
{
    // Plausible future Amazon wire value absent from every ErrorType member name
    // (and from member-name matching in any casing), so it can never silently start
    // mapping and turn the tolerance test into a known-value test.
    private const string UnknownWireType = "MEDIA_ERROR_IO_LAG";

    private readonly Mock<ISessionManager> _sessionManagerMock = new();
    private readonly PluginConfiguration _config = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(_ => { });
    private readonly DeviceQueueManager _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillRequestDeserializerTests"/> class.
    /// </summary>
    public SkillRequestDeserializerTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager("SkillRequestDeserializerTests");
    }

    /// <inheritdoc/>
    public void Dispose() => _queueManager.Dispose();

    [Fact]
    public void UnknownErrorType_DoesNotKillDeserialization_AndReachesPlaybackFailedHandler()
    {
        string body = PlaybackFailedBody(UnknownWireType, token: Guid.NewGuid().ToString(), requestId: "req-unknown-1");

        var request = SkillRequestDeserializer.Deserialize(body);

        Assert.NotNull(request);
        AudioPlayerRequest audioPlayerRequest = Assert.IsType<AudioPlayerRequest>(request!.Request);
        Assert.Equal(AudioRequestType.PlaybackFailed, audioPlayerRequest.AudioRequestType);

        var handler = new PlaybackFailedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.True(handler.CanHandle(request.Request));
    }

    [Fact]
    public void UnknownErrorType_PreservesRawWireValue_AndBucketsToInternalError()
    {
        string body = PlaybackFailedBody(UnknownWireType, token: Guid.NewGuid().ToString(), requestId: "req-unknown-2");

        var request = SkillRequestDeserializer.Deserialize(body);

        var audioPlayerRequest = Assert.IsType<AudioPlayerRequest>(request!.Request);
        var tolerated = SkillRequestDeserializer.FindToleratedError(request);
        Assert.NotNull(tolerated);
        Assert.Same(audioPlayerRequest.Error, tolerated);
        Assert.Equal(UnknownWireType, tolerated!.RawWireType);
        Assert.Equal(ErrorType.InternalError, tolerated.Type);
        Assert.Equal("The media pipeline encountered an error", tolerated.Message);
    }

    [Fact]
    public void KnownErrorType_MapsToTheSameEnumMember_ThroughTheProductionAndTheNaivePaths()
    {
        // MediaServiceUnavailable is in the mapped vocabulary (member-name matching).
        // The production seam must keep the mapped path byte-identical: the same body
        // deserializes to the same enum member with and without the tolerant settings.
        string body = PlaybackFailedBody("MediaServiceUnavailable", token: Guid.NewGuid().ToString(), requestId: "req-known-1");

        var request = SkillRequestDeserializer.Deserialize(body);
        var audioPlayerRequest = Assert.IsType<AudioPlayerRequest>(request!.Request);
        Assert.Equal(ErrorType.MediaServiceUnavailable, audioPlayerRequest.Error!.Type);
        Assert.Null(SkillRequestDeserializer.FindToleratedError(request));
        Assert.Equal("MediaServiceUnavailable", UnknownWireTypeError.DescribeWireType(audioPlayerRequest.Error));

        var naive = JsonConvert.DeserializeObject<SkillRequest>(body);
        var naiveAudio = Assert.IsType<AudioPlayerRequest>(naive!.Request);
        Assert.Equal(ErrorType.MediaServiceUnavailable, naiveAudio.Error!.Type);
    }

    [Fact]
    public void KnownWireFormErrorType_MapsViaEnumMember_ThroughTheProductionAndTheNaivePaths()
    {
        // The EnumMember wire names are the other half of the mapped vocabulary: the
        // SCREAMING_SNAKE form every Amazon error table uses must keep mapping through
        // the tolerant seam exactly as it did through the naive path (no tolerance
        // bucket, no raw-value stash, byte-identical member).
        string wireForm = "MEDIA_ERROR_SERVICE_UNAVAILABLE";
        string body = PlaybackFailedBody(wireForm, token: Guid.NewGuid().ToString(), requestId: "req-known-2");

        var request = SkillRequestDeserializer.Deserialize(body);
        var audioPlayerRequest = Assert.IsType<AudioPlayerRequest>(request!.Request);
        Assert.Equal(ErrorType.MediaServiceUnavailable, audioPlayerRequest.Error!.Type);
        Assert.Null(SkillRequestDeserializer.FindToleratedError(request));

        var naive = JsonConvert.DeserializeObject<SkillRequest>(body);
        var naiveAudio = Assert.IsType<AudioPlayerRequest>(naive!.Request);
        Assert.Equal(ErrorType.MediaServiceUnavailable, naiveAudio.Error!.Type);
    }

    [Fact]
    public void UnknownErrorType_NaivePath_Throws_PremiseCanary()
    {
        // Pins the premise the tolerance exists for: without the tolerant settings
        // (the pre-JF-931 call shape) an unknown error.type kills the whole request.
        // If this test ever greens, Alexa.NET gained native tolerance and
        // ErrorTypeTolerantConverter deserves a simplification pass.
        string body = PlaybackFailedBody(UnknownWireType, token: Guid.NewGuid().ToString(), requestId: "req-unknown-3");

        Assert.ThrowsAny<JsonException>(
            () => JsonConvert.DeserializeObject<SkillRequest>(body));
    }

    [Fact]
    public async Task UnknownErrorType_HandlerRuns_FlagClearAndOrderingRegistrationNotSkipped()
    {
        // JF-655 flag clear + JF-447 ordering registration: the side effects the old
        // deserialization death silently skipped. Fresh device with the active-audio
        // flag set and no start recorded: the failure is a real (non-displacement) stop,
        // so the flag MUST clear and the stop MUST be reported.
        string deviceId = $"jf931-{Guid.NewGuid():N}";
        Guid itemId = Guid.NewGuid();
        string token = itemId.ToString();
        string body = PlaybackFailedBody(UnknownWireType, token, requestId: "req-unknown-4");
        var context = TestHelpers.CreateTestContext(deviceId);

        _queueManager.MarkAudioPlaybackStarted(deviceId);
        Assert.True(_queueManager.IsAudioPlaybackActive(deviceId));

        var request = SkillRequestDeserializer.Deserialize(body);
        var handler = new PlaybackFailedEventHandler(_sessionManagerMock.Object, _config, _loggerFactory, _queueManager);

        var response = await handler.HandleAsync(
            request!.Request, context, TestHelpers.CreateTestUser(), TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response!.ShouldEndSession != false, "PlaybackFailed must return a keep-alive ack shape (JF-299)");
        Assert.False(_queueManager.IsAudioPlaybackActive(deviceId), "the JF-655 active-audio flag must clear for a tolerated unknown error.type");
        _sessionManagerMock.Verify(
            s => s.OnPlaybackStopped(It.Is<PlaybackStopInfo>(i => i.ItemId == itemId && i.Failed)),
            Times.Once);
    }

    [Fact]
    public void DescribeWireType_RendersUnknownSentinelForAbsentPayload()
    {
        Assert.Equal("UNKNOWN", UnknownWireTypeError.DescribeWireType(null));
    }

    [Fact]
    public void ToleratedError_FoundOnEveryCarrier()
    {
        // The Error payload rides three request shapes; the controller's tolerated-value
        // warning must see an unknown value on each of them.
        var audio = SkillRequestDeserializer.Deserialize(
            PlaybackFailedBody(UnknownWireType, token: Guid.NewGuid().ToString(), requestId: "req-carrier-audio"));
        Assert.NotNull(SkillRequestDeserializer.FindToleratedError(audio));

        var sessionEnded = SkillRequestDeserializer.Deserialize(SessionEndedBody("BRAND_NEW_SESSION_ERROR"));
        var endedRequest = Assert.IsType<SessionEndedRequest>(sessionEnded!.Request);
        var endedTolerated = SkillRequestDeserializer.FindToleratedError(sessionEnded);
        Assert.NotNull(endedTolerated);
        Assert.Same(endedRequest.Error, endedTolerated);
        Assert.Equal("BRAND_NEW_SESSION_ERROR", endedTolerated!.RawWireType);

        var systemException = SkillRequestDeserializer.Deserialize(SystemExceptionBody(UnknownWireType));
        var exceptionRequest = Assert.IsType<SystemExceptionRequest>(systemException!.Request);
        var exceptionTolerated = SkillRequestDeserializer.FindToleratedError(systemException);
        Assert.NotNull(exceptionTolerated);
        Assert.Same(exceptionRequest.Error, exceptionTolerated);
        Assert.Equal(UnknownWireType, exceptionTolerated!.RawWireType);
    }

    [Fact]
    public async Task NonObjectErrorPayload_ToleratedWithoutKillingTheRequest_AndExceptionHandlerCompletes()
    {
        // A malformed (string, not object) error payload must neither die at
        // deserialization nor come back as a null Error: ExceptionHandler reads
        // Error.Type unguarded, so null would trade the clean pre-JF-931 deserialization
        // failure for a NullReferenceException.
        string body =
            "{" +
            "\"version\":\"1.0\"," +
            "\"context\":{}," +
            "\"request\":{" +
              "\"type\":\"System.ExceptionEncountered\"," +
              "\"requestId\":\"req-string-error\"," +
              "\"error\":\"MEDIA_ERROR_X\"" +
            "}" +
          "}";

        var request = SkillRequestDeserializer.Deserialize(body);
        var exceptionRequest = Assert.IsType<SystemExceptionRequest>(request!.Request);
        var tolerated = Assert.IsType<UnknownWireTypeError>(exceptionRequest.Error);
        Assert.Equal(ErrorType.InternalError, tolerated.Type);
        Assert.Contains("MEDIA_ERROR_X", tolerated.RawWireType);

        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.True(handler.CanHandle(request.Request));
        var response = await handler.HandleAsync(
            request.Request,
            TestHelpers.CreateTestContext($"jf931-string-{Guid.NewGuid():N}"),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);
        Assert.NotNull(response);
    }

    /// <summary>
    /// Build a realistic AudioPlayer.PlaybackFailed wire body.
    /// </summary>
    /// <param name="errorType">The raw error.type wire value.</param>
    /// <param name="token">The stream token.</param>
    /// <param name="requestId">The request id.</param>
    /// <returns>The raw body JSON.</returns>
    private static string PlaybackFailedBody(string errorType, string token, string requestId) =>
        "{" +
        "\"version\":\"1.0\"," +
        "\"context\":{" +
          "\"System\":{" +
            "\"application\":{\"applicationId\":\"amzn1.ask.skill.1111\"}," +
            "\"user\":{\"userId\":\"amzn1.ask.account.test\",\"accessToken\":\"3e6fb1c2-9f2a-4f34-9d3e-1c9a11e2c001\"}," +
            "\"device\":{\"deviceId\":\"device-1\"}" +
          "}," +
          "\"AudioPlayer\":{\"token\":\"" + token + "\",\"offsetInMilliseconds\":5000,\"playerActivity\":\"PLAYING\"}" +
        "}," +
        "\"request\":{" +
          "\"type\":\"AudioPlayer.PlaybackFailed\"," +
          "\"requestId\":\"" + requestId + "\"," +
          "\"timestamp\":\"2026-11-20T09:00:00Z\"," +
          "\"token\":\"" + token + "\"," +
          "\"offsetInMilliseconds\":5000," +
          "\"locale\":\"en-US\"," +
          "\"error\":{" +
            "\"type\":\"" + errorType + "\"," +
            "\"message\":\"The media pipeline encountered an error\"" +
          "}" +
        "}" +
      "}";

    /// <summary>
    /// Build a realistic SessionEndedRequest wire body carrying an error payload.
    /// </summary>
    /// <param name="errorType">The raw error.type wire value.</param>
    /// <returns>The raw body JSON.</returns>
    private static string SessionEndedBody(string errorType) =>
        "{" +
        "\"version\":\"1.0\"," +
        "\"context\":{}," +
        "\"request\":{" +
          "\"type\":\"SessionEndedRequest\"," +
          "\"requestId\":\"req-session-ended-1\"," +
          "\"reason\":\"ERROR\"," +
          "\"error\":{\"type\":\"" + errorType + "\",\"message\":\"session ended with error\"}" +
        "}" +
      "}";

    /// <summary>
    /// Build a realistic System.ExceptionEncountered wire body carrying an error payload.
    /// </summary>
    /// <param name="errorType">The raw error.type wire value.</param>
    /// <returns>The raw body JSON.</returns>
    private static string SystemExceptionBody(string errorType) =>
        "{" +
        "\"version\":\"1.0\"," +
        "\"context\":{}," +
        "\"request\":{" +
          "\"type\":\"System.ExceptionEncountered\"," +
          "\"requestId\":\"req-system-exception-1\"," +
          "\"source\":\"Alexa.AudioPlayer\"," +
          "\"error\":{\"type\":\"" + errorType + "\",\"message\":\"playback error surfaced as a system exception\"}" +
        "}" +
      "}";
}

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
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
/// The JF-957 tests extend the same seam to SessionEndedRequest.Reason, whose
/// property-level StringEnumConverter killed unknown reason values the same way.
/// Its seam is the contract resolver's property-converter replacement (the
/// RequestConverter news the instance and serializer.Populate consults converters
/// neither for the root type nor against the property attribute), and its raw wire
/// value rides an instance-keyed tolerance table instead of a value subclass (an enum
/// slot cannot carry one).
/// </summary>
[Collection("Plugin")]
public class SkillRequestDeserializerTests : IDisposable
{
    // Plausible future Amazon wire value absent from every ErrorType member name
    // (and from member-name matching in any casing), so it can never silently start
    // mapping and turn the tolerance test into a known-value test.
    private const string UnknownWireType = "MEDIA_ERROR_IO_LAG";

    // Same discipline for the reason vocabulary (JF-957): a plausible future
    // SessionEndedRequest.Reason value absent from every Reason member name (and from
    // member-name matching in any casing).
    private const string UnknownWireReason = "SESSION_TIMED_OUT";

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
        // The known-value branch renders the EnumMember wire form (the vocabulary
        // Amazon's error tables use), not the member name, so log lines self-correlate
        // with the docs (JF-930).
        Assert.Equal("MEDIA_ERROR_SERVICE_UNAVAILABLE", UnknownWireTypeError.DescribeWireType(audioPlayerRequest.Error));

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

    [Fact]
    public async Task ExplicitNullErrorPayload_IsTolerated_AndExceptionHandlerCompletes()
    {
        // An explicit "error": null deserializes to a null Error (the JF-931
        // converter's null branch). ExceptionHandler used to dereference Error.Type
        // unguarded, turning the tolerated payload into a NullReferenceException; it
        // must instead classify the InternalError bucket and let the shared renderer
        // print UNKNOWN (JF-930, code-review round).
        string body =
            "{" +
            "\"version\":\"1.0\"," +
            "\"context\":{}," +
            "\"request\":{" +
              "\"type\":\"System.ExceptionEncountered\"," +
              "\"requestId\":\"req-null-error\"," +
              "\"error\":null" +
            "}" +
          "}";

        var request = SkillRequestDeserializer.Deserialize(body);
        var exceptionRequest = Assert.IsType<SystemExceptionRequest>(request!.Request);
        Assert.Null(exceptionRequest.Error);

        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.True(handler.CanHandle(request.Request));
        var response = await handler.HandleAsync(
            request.Request,
            TestHelpers.CreateTestContext($"jf930-null-{Guid.NewGuid():N}"),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);
        Assert.NotNull(response);
    }

    [Fact]
    public void DescribeWireType_EveryEnumMember_RendersTheWireForm_NotTheMemberName()
    {
        // Pins the JF-930 vocabulary contract: every ErrorType member of the pinned
        // Alexa.NET carries an EnumMember wire name, so no known value ever falls to
        // the member-name fallback in DescribeWireType. When a future library version
        // ships an unattributed member, this test fails and forces a conscious
        // decision instead of a silent vocabulary split on the log lines.
        foreach (ErrorType member in Enum.GetValues<ErrorType>())
        {
            var error = new Error { Type = member };
            Assert.True(
                UnknownWireTypeError.DescribeWireType(error) != member.ToString(),
                $"{member} has no EnumMember wire name; it would render as the member name on log lines, splitting the wire vocabulary");
        }
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
    /// Build a realistic SessionEndedRequest wire body with a parameterized reason
    /// (JF-957): the reason slot is the carrier under test here, so no error payload.
    /// </summary>
    /// <param name="reason">The raw reason wire value.</param>
    /// <param name="requestId">The request id.</param>
    /// <returns>The raw body JSON.</returns>
    private static string SessionEndedBodyWithReason(string reason, string requestId) =>
        "{" +
        "\"version\":\"1.0\"," +
        "\"context\":{}," +
        "\"request\":{" +
          "\"type\":\"SessionEndedRequest\"," +
          "\"requestId\":\"" + requestId + "\"," +
          "\"reason\":\"" + reason + "\"" +
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

    [Fact]
    public void ExplicitNullType_IsToleratedNotSilentlyDefaulted()
    {
        // Review round: an explicit "type": null must ride the tolerated path
        // (pre-fix the naive call failed loudly on it); silently producing a
        // plain Error with no Warn trace would contradict the fix's principle.
        string body = "{" +
            "\"version\":\"1.0\",\"context\":{}," +
            "\"request\":{" +
              "\"type\":\"AudioPlayer.PlaybackFailed\"," +
              "\"requestId\":\"req-null-type\"," +
              "\"token\":\"tok\"," +
              "\"error\":{\"type\":null,\"message\":\"m\"}" +
            "}" +
          "}";
        var req = SkillRequestDeserializer.Deserialize(body);
        var tolerated = SkillRequestDeserializer.FindToleratedError(req);
        Assert.NotNull(tolerated);
        Assert.Equal(ErrorType.InternalError, tolerated.Type);
    }

    [Fact]
    public void StringErrorPayload_RendersRawValueUnquoted()
    {
        // Review round: a non-object string payload must keep the same unquoted
        // rendering contract the object branch uses for string type tokens.
        string body = "{" +
            "\"version\":\"1.0\",\"context\":{}," +
            "\"request\":{" +
              "\"type\":\"AudioPlayer.PlaybackFailed\"," +
              "\"requestId\":\"req-string-error\"," +
              "\"token\":\"tok\"," +
              "\"error\":\"MEDIA_ERROR_X\"" +
            "}" +
          "}";
        var req = SkillRequestDeserializer.Deserialize(body);
        var tolerated = SkillRequestDeserializer.FindToleratedError(req);
        Assert.NotNull(tolerated);
        Assert.Equal("MEDIA_ERROR_X", tolerated!.RawWireType);
        Assert.DoesNotContain("\"", UnknownWireTypeError.DescribeWireType(tolerated));
    }

    // ==== JF-957: tolerated unknown SessionEndedRequest.Reason values ====

    [Fact]
    public void UnknownReason_DoesNotKillDeserialization_AndReachesSessionEndedHandler()
    {
        string body = SessionEndedBodyWithReason(UnknownWireReason, "req-unknown-reason-1");

        var request = SkillRequestDeserializer.Deserialize(body);

        Assert.NotNull(request);
        Assert.IsType<SessionEndedRequest>(request!.Request);

        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        Assert.True(handler.CanHandle(request.Request));
    }

    [Fact]
    public void UnknownReason_PreservesRawWireValue_AndBucketsToError()
    {
        string body = SessionEndedBodyWithReason(UnknownWireReason, "req-unknown-reason-2");

        var request = SkillRequestDeserializer.Deserialize(body);
        var endedRequest = Assert.IsType<SessionEndedRequest>(request!.Request);
        var tolerated = SkillRequestDeserializer.FindToleratedReason(request);
        Assert.NotNull(tolerated);
        Assert.Equal(UnknownWireReason, tolerated!.RawWireReason);
        Assert.Equal(Reason.Error, tolerated.Reason);
        Assert.Equal(Reason.Error, endedRequest.Reason);
        Assert.Equal(UnknownWireReason + " (mapped to Error)", UnknownWireTypeReason.DescribeReason(endedRequest));
    }

    [Fact]
    public async Task UnknownReason_SessionEndedHandlerRuns_AndCompletesEmpty()
    {
        string body = SessionEndedBodyWithReason(UnknownWireReason, "req-unknown-reason-3");

        var request = SkillRequestDeserializer.Deserialize(body);
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, _loggerFactory);

        var response = await handler.HandleAsync(
            request!.Request,
            TestHelpers.CreateTestContext($"jf957-{Guid.NewGuid():N}"),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response!.OutputSpeech);
    }

    [Fact]
    public void KnownReason_MapsToTheSameEnumMember_ThroughTheProductionAndTheNaivePaths()
    {
        // The member-name form is in the mapped vocabulary. The production seam must
        // keep the mapped path byte-identical: the same body deserializes to the same
        // enum member with and without the tolerant settings.
        string body = SessionEndedBodyWithReason("Error", "req-known-reason-1");

        var request = SkillRequestDeserializer.Deserialize(body);
        var endedRequest = Assert.IsType<SessionEndedRequest>(request!.Request);
        Assert.Equal(Reason.Error, endedRequest.Reason);
        Assert.Null(SkillRequestDeserializer.FindToleratedReason(request));
        Assert.Equal("Error", UnknownWireTypeReason.DescribeReason(endedRequest));

        var naive = JsonConvert.DeserializeObject<SkillRequest>(body);
        var naiveEnded = Assert.IsType<SessionEndedRequest>(naive!.Request);
        Assert.Equal(Reason.Error, naiveEnded.Reason);
    }

    [Fact]
    public void KnownWireFormReason_MapsViaEnumMember_ThroughTheProductionAndTheNaivePaths()
    {
        // The EnumMember wire names are the other half of the mapped vocabulary: the
        // SCREAMING_SNAKE form the Amazon session-ended tables use must keep mapping
        // through the tolerant seam exactly as it did through the naive path (no
        // tolerance bucket, no raw-value stash, byte-identical member).
        string body = SessionEndedBodyWithReason("USER_INITIATED", "req-known-reason-2");

        var request = SkillRequestDeserializer.Deserialize(body);
        var endedRequest = Assert.IsType<SessionEndedRequest>(request!.Request);
        Assert.Equal(Reason.UserInitiated, endedRequest.Reason);
        Assert.Null(SkillRequestDeserializer.FindToleratedReason(request));

        var naive = JsonConvert.DeserializeObject<SkillRequest>(body);
        var naiveEnded = Assert.IsType<SessionEndedRequest>(naive!.Request);
        Assert.Equal(Reason.UserInitiated, naiveEnded.Reason);
    }

    [Fact]
    public void UnknownReason_NaivePath_Throws_PremiseCanary()
    {
        // Pins the premise the tolerance exists for: without the tolerant settings
        // (the pre-JF-957 call shape) an unknown reason kills the whole request.
        // If this test ever greens, Alexa.NET gained native tolerance and the
        // ReasonTolerantConverter seam deserves a simplification pass.
        string body = SessionEndedBodyWithReason(UnknownWireReason, "req-unknown-reason-4");

        Assert.ThrowsAny<JsonException>(
            () => JsonConvert.DeserializeObject<SkillRequest>(body));
    }

    [Fact]
    public void ExplicitNullReason_IsToleratedNotSilentlyDefaulted()
    {
        // Mirrors the JF-931 explicit-null convention: the pre-fix naive contract
        // failed loudly on "reason": null ("Cannot convert null value to Reason"),
        // so a silent default with no Warn trace would contradict the fix's principle.
        string body = "{" +
            "\"version\":\"1.0\",\"context\":{}," +
            "\"request\":{" +
              "\"type\":\"SessionEndedRequest\"," +
              "\"requestId\":\"req-null-reason\"," +
              "\"reason\":null" +
            "}" +
          "}";
        var req = SkillRequestDeserializer.Deserialize(body);
        var endedRequest = Assert.IsType<SessionEndedRequest>(req!.Request);
        var tolerated = SkillRequestDeserializer.FindToleratedReason(req);
        Assert.NotNull(tolerated);
        Assert.Equal("null", tolerated!.RawWireReason);
        Assert.Equal(Reason.Error, tolerated!.Reason);
        Assert.Equal(Reason.Error, endedRequest.Reason);
    }

    [Fact]
    public void ToleratedReason_IsInstanceKeyed_AndAbsentOnOtherCarriers()
    {
        // The raw wire value is keyed on the request instance (an enum slot cannot
        // carry a subclass like the Error carrier does), so two session-ended
        // requests tolerate their own values independently and no other request
        // shape ever reports a tolerated reason.
        var first = SkillRequestDeserializer.Deserialize(SessionEndedBodyWithReason(UnknownWireReason, "req-keyed-1"));
        var second = SkillRequestDeserializer.Deserialize(SessionEndedBodyWithReason("NEW_ALEXA_REASON", "req-keyed-2"));

        Assert.Equal(UnknownWireReason, SkillRequestDeserializer.FindToleratedReason(first)!.RawWireReason);
        Assert.Equal("NEW_ALEXA_REASON", SkillRequestDeserializer.FindToleratedReason(second)!.RawWireReason);
        Assert.Equal(UnknownWireReason, SkillRequestDeserializer.FindToleratedReason(first)!.RawWireReason);

        var audio = SkillRequestDeserializer.Deserialize(
            PlaybackFailedBody(UnknownWireType, token: Guid.NewGuid().ToString(), requestId: "req-keyed-audio"));
        Assert.Null(SkillRequestDeserializer.FindToleratedReason(audio));
    }

    [Fact]
    public void MissingReasonKey_StaysNaiveDefault_WithoutTolerance()
    {
        // Pinning the missing-key stance: a body with NO reason key never reaches the
        // tolerant converter (Populate skips absent properties), so the request keeps
        // the C# enum default UserInitiated with no tolerated entry and no Warn, the
        // exact pre-fix behavior. The converter's doc names why this is accepted
        // (Amazon always sends the field; a silent default is indistinguishable from
        // the wire's own user-initiated semantics). If this ever changes, it is a
        // conscious decision, not drift.
        string body = "{" +
            "\"version\":\"1.0\",\"context\":{}," +
            "\"request\":{" +
              "\"type\":\"SessionEndedRequest\"," +
              "\"requestId\":\"req-missing-reason\"" +
            "}" +
          "}";
        var req = SkillRequestDeserializer.Deserialize(body);
        var endedRequest = Assert.IsType<SessionEndedRequest>(req!.Request);
        Assert.Equal(Reason.UserInitiated, endedRequest.Reason);
        Assert.Null(SkillRequestDeserializer.FindToleratedReason(req));
    }

    [Fact]
    public void ToleratedReasonDrain_GuardedAgainstDuplicateSetValue()
    {
        // The drain must be failure-tolerant: the current no-collision guarantee rests
        // on two external behaviors (JObject.Load collapsing duplicate keys, one fresh
        // instance per Populate), so the insert is guarded (remove-then-add). A future
        // Alexa.NET change that populated one request instance twice must land as
        // last-write-wins, never as an ArgumentException killing the request at
        // deserialization, the exact death class this fix exists to eliminate.
        var request = new SessionEndedRequest { Reason = Reason.Error };
        var provider = new TolerantReasonValueProvider(new NoopValueProvider());
        var wrapper = new SkillRequest { Request = request };

        ReasonTolerantConverter.PendingUnknownWireReason = new UnknownWireTypeReason("FIRST_REASON", Reason.Error);
        provider.SetValue(request, Reason.Error);
        ReasonTolerantConverter.PendingUnknownWireReason = new UnknownWireTypeReason("SECOND_REASON", Reason.Error);
        provider.SetValue(request, Reason.Error);

        // Each SetValue drains the stash it just drained; the surviving keyed value is
        // the last write, and the thread stash is clean for the next deserialization.
        Assert.Null(ReasonTolerantConverter.PendingUnknownWireReason);
        Assert.Equal("SECOND_REASON", SkillRequestDeserializer.FindToleratedReason(wrapper)!.RawWireReason);
    }

    /// <summary>
    /// A do-nothing inner value provider for driving the tolerant drain directly: the
    /// tests here exercise the wrapper's stash-drain contract, not the reflection set.
    /// </summary>
    private sealed class NoopValueProvider : IValueProvider
    {
        /// <inheritdoc/>
        public object? GetValue(object target) => null;

        /// <inheritdoc/>
        public void SetValue(object target, object? value)
        {
        }
    }

    [Fact]
    public void DescribeReason_RendersUnknownSentinelForAbsentRequest()
    {
        Assert.Equal("UNKNOWN", UnknownWireTypeReason.DescribeReason(null));
    }

    public class SliceBodyExcerptTests
    {
        // Review round: the excerpt-cut rules pinned as a pure function test.
        [Fact]
        public void ShortBody_PassesThroughUnchanged()
        {
            Assert.Equal("abc", AlexaSkillController.SliceBodyExcerpt("abc", 4000));
        }

        [Fact]
        public void LongBody_IsCapped()
        {
            string body = new string('a', 5000);
            Assert.Equal(4000, AlexaSkillController.SliceBodyExcerpt(body, 4000).Length);
        }

        [Fact]
        public void CutOnHighSurrogate_StepsBackOne()
        {
            // Build a body whose 4000th char is a HIGH surrogate: the cut must
            // step back so the excerpt never ends on a lone (invalid) half pair.
            string pair = "😀"; // U+1F600 as a surrogate pair
            string prefix = new string('a', 3999);
            string body = prefix + pair + new string('b', 10);
            string excerpt = AlexaSkillController.SliceBodyExcerpt(body, 4000);
            Assert.Equal(3999, excerpt.Length);
            Assert.Equal(prefix, excerpt);
        }

        [Fact]
        public void CutNotOnSurrogate_KeepsTheFullCap()
        {
            string pair = "😀";
            string body = new string('a', 3998) + pair + new string('b', 10);
            // The cap cuts inside the b-run, no step-back needed: 4000 chars.
            Assert.Equal(4000, AlexaSkillController.SliceBodyExcerpt(body, 4000).Length);
        }
    }

}

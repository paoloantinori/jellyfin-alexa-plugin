using System;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-708: the ONE event-aware degrade core pinned across every surface's shape.
/// The four former inline copies (BaseHandler.BuildUserNotFoundResponse,
/// BaseHandler.BuildSessionMissResponse, the controller's catch-path degrades, and
/// the RequestPipeline refusal translation) all route through
/// <see cref="BaseHandler.DegradeForEventRequest(Request?, string)"/>; these pins
/// hold the core's own contract, which is what each folded surface inherits: a
/// null request (the controller's deserialization-failure catch paths) and every
/// non-event request keep the exact Tell; each event request class (AudioPlayer
/// event, SessionEnded, SystemException) gets the speechless keep-alive (JF-507:
/// Amazon rejects outputSpeech on event responses with INVALID_RESPONSE); and the
/// Func overload never invokes the non-event factory on event requests (the
/// session-miss laziness: its dead-token branch reads DeviceQueueManager and
/// writes the AccountRelink diagnostic log, which must not fire on events).
/// The folded surfaces' end-to-end shapes are pinned where they live
/// (EventHandlerTests' JF-507/JF-527 set, PipelineTests' refusal set,
/// SkillWarmingUpTests' warming set).
/// </summary>
public class EventAwareDegradeCoreTests
{
    private const string Message = "degrade message under test";

    [Fact]
    public void NullRequest_AnswersTheTell()
    {
        SkillResponse response = BaseHandler.DegradeForEventRequest(null, Message);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(Message, speech.Text);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void IntentRequest_AnswersTheExactTell()
    {
        var request = new IntentRequest { Intent = new Intent { Name = "AnyIntent" } };

        SkillResponse response = BaseHandler.DegradeForEventRequest(request, Message);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(Message, speech.Text);
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// Every request class <see cref="BaseHandler.IsEventRequest"/> classifies gets
    /// the keep-alive: no speech, no card, no directives, ShouldEndSession null.
    /// </summary>
    [Theory]
    [MemberData(nameof(EventRequests))]
    public void EventRequestClasses_AnswerKeepAlive(Request eventRequest)
    {
        SkillResponse response = BaseHandler.DegradeForEventRequest(eventRequest, Message);

        Assert.Null(response.Response.OutputSpeech);
        Assert.Null(response.Response.Card);
        Assert.Null(response.Response.Reprompt);
        Assert.Null(response.Response.ShouldEndSession);
        Assert.Empty(response.Response.Directives);
    }

    public static TheoryData<Request> EventRequests => new()
    {
        new AudioPlayerRequest(),
        new SessionEndedRequest(),
        new SystemExceptionRequest()
    };

    /// <summary>
    /// The session-miss laziness contract: the non-event factory must never run for
    /// an event request (eager evaluation would fire its diagnostic log and queue
    /// read on events, the behavior the pre-JF-708 early return guaranteed).
    /// </summary>
    [Fact]
    public void FuncOverload_FactoryNotInvokedOnEventRequest()
    {
        bool invoked = false;

        SkillResponse response = BaseHandler.DegradeForEventRequest(
            new AudioPlayerRequest(),
            () =>
            {
                invoked = true;
                return ResponseBuilder.Tell(Message);
            });

        Assert.False(invoked, "the non-event factory must not run for an event request");
        Assert.Null(response.Response.OutputSpeech);
        Assert.Null(response.Response.ShouldEndSession);
    }

    [Fact]
    public void FuncOverload_FactoryInvokedOnNonEventRequest()
    {
        bool invoked = false;

        SkillResponse response = BaseHandler.DegradeForEventRequest(
            new IntentRequest(),
            () =>
            {
                invoked = true;
                return ResponseBuilder.Tell(Message);
            });

        Assert.True(invoked);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(Message, speech.Text);
    }

    /// <summary>
    /// The Func overload answers null requests with the factory too (the string
    /// overload delegates to it, so the null-request decision lives in ONE place).
    /// </summary>
    [Fact]
    public void FuncOverload_NullRequest_InvokesFactory()
    {
        bool invoked = false;

        SkillResponse response = BaseHandler.DegradeForEventRequest(
            null,
            () =>
            {
                invoked = true;
                return ResponseBuilder.Tell(Message);
            });

        Assert.True(invoked);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(Message, speech.Text);
    }
}

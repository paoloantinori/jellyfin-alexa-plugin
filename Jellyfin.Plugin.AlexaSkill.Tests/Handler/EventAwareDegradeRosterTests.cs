using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-708 structural pin: the event-aware degrade decision lives in ONE place.
/// The only plugin method allowed to reference BOTH
/// <see cref="BaseHandler.IsEventRequest"/> AND
/// <see cref="BaseHandler.BuildKeepAliveResponse"/> is the shared core's Func
/// overload; every folded surface (the user-not-found and session-miss degrades,
/// the controller's four catch paths, the RequestPipeline refusal translation)
/// calls <see cref="BaseHandler.DegradeForEventRequest(Request?, string)"/> or its
/// Func twin instead, the event handlers' own keep-alive returns reference only
/// BuildKeepAliveResponse (no decision), and the circuit-breaker short-circuit
/// references only IsEventRequest. A FIFTH inline copy (a new method hand-rolling
/// the `IsEventRequest ? BuildKeepAliveResponse() : Tell` ternary, the pre-JF-708
/// drift class) fails this roster loudly instead of regrowing silently.
/// SELF-RED: inlining the core's body back into any folded site (the JF-708
/// revert shape) flips this test by itself.
/// ACCEPTED BOUNDARY (the roster idiom's documented-limit class, same as the other
/// IlCallScanner rosters): ground truth is the IL call/callvirt stream, so a
/// method reaching the two members only via reflection emits no call instructions
/// and escapes the scan; same-assembly methoddef tokens only (the scanner's own
/// constraint). SECOND BOUNDARY (JF-708 gate-marker): an INLINE pattern evasion,
/// a copy that spells the type test itself (request is AudioPlayerRequest or
/// SessionEndedRequest or SystemExceptionRequest) while calling
/// BuildKeepAliveResponse directly, references only one scanned member and also
/// escapes the conjunction; the site-level side-effect pins that pair with this
/// boundary live in EventHandlerTests' JF-752 set (the session-miss site, the
/// one folded surface whose non-event factory has observable side effects; the
/// other three build a pure single Tell on either leg, so a hoist there has
/// no side effect to fire).
/// </summary>
public class EventAwareDegradeRosterTests
{
    [Fact]
    public void IsEventAndKeepAliveConjunction_LivesOnlyInTheDegradeCore()
    {
        HashSet<int> isEventTokens = IlCallScanner
            .MethodTokens(typeof(BaseHandler), nameof(BaseHandler.IsEventRequest)).ToHashSet();
        HashSet<int> keepAliveTokens = IlCallScanner
            .MethodTokens(typeof(BaseHandler), nameof(BaseHandler.BuildKeepAliveResponse)).ToHashSet();
        Assert.NotEmpty(isEventTokens);
        Assert.NotEmpty(keepAliveTokens);

        var offenders = new SortedSet<string>();
        foreach ((Type type, System.Reflection.MethodBase method) in IlCallScanner.DeclaredMethods(typeof(BaseHandler).Assembly))
        {
            if (IlCallScanner.ContainsCallToAnyToken(method, isEventTokens)
                && IlCallScanner.ContainsCallToAnyToken(method, keepAliveTokens))
            {
                offenders.Add($"{type.FullName}.{IlCallScanner.LogicalMethodName(method)}");
            }
        }

        var expected = new SortedSet<string> { $"{typeof(BaseHandler).FullName}.{nameof(BaseHandler.DegradeForEventRequest)}" };

        Assert.True(
            expected.SetEquals(offenders),
            "The IsEventRequest+BuildKeepAliveResponse conjunction drifted from the one-core roster. " +
            (offenders.Except(expected).Any()
                ? $"Methods hand-rolling the event-aware degrade instead of calling BaseHandler.DegradeForEventRequest: [{string.Join(", ", offenders.Except(expected))}]. Fold them onto the shared core (JF-708). "
                : string.Empty) +
            (expected.Except(offenders).Any()
                ? $"The core no longer references both members as expected: [{string.Join(", ", expected.Except(offenders))}] was renamed or restructured; update this roster."
                : string.Empty));
    }
}

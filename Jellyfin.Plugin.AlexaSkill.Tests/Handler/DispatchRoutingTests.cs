using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Routing-level coverage tests (JF-433). Prove that the dispatch layer (CanHandle +
/// registration order + force-route) behaves as expected and catches the failure classes
/// invisible to direct HandleAsync tests (dead handler code, dispatch-order drift,
/// force-route bugs).
/// </summary>
[Collection("Plugin")]
public class DispatchRoutingTests : PluginTestBase
{

    [Fact]
    public void Handlers_Registered_FallbackLast_OthersAlphabetical()
    {
        // Pins the ordering semantics the controller relies on (first CanHandle match
        // in registration order wins): FallbackIntentHandler last, everything else
        // alphabetical by type name.
        var handlerTypes = DispatchHarness.RegisteredHandlerTypes();

        Assert.Same(typeof(FallbackIntentHandler), handlerTypes[^1]);

        var nonFallback = handlerTypes.Take(handlerTypes.Count - 1).ToList();
        Assert.Equal(
            nonFallback.OrderBy(t => t.Name).ToList(),
            nonFallback);
    }

    [Fact]
    public void RealRegistrator_RegistersHandlers_InHarnessOrder()
    {
        // The strongest mirror check: run the REAL Registrator against a real DI
        // service collection and compare the BaseHandler registration descriptors to
        // the harness's order. Without this, a Registrator edit (filter, ordering,
        // service type) would drift production dispatch while the harness stays green,
        // which is exactly the incident class this harness exists to catch.
        var services = new ServiceCollection();
        new Jellyfin.Plugin.AlexaSkill.EntryPoints.Registrator()
            .RegisterServices(services, Mock.Of<MediaBrowser.Controller.IServerApplicationHost>());

        List<Type> registeredByRegistrator = services
            .Where(d => d.ServiceType == typeof(BaseHandler) && d.ImplementationType != null)
            .Select(d => d.ImplementationType!)
            .ToList();

        Assert.Equal(
            DispatchHarness.RegisteredHandlerTypes(),
            registeredByRegistrator);
    }

    [Fact]
    public void AllHandlers_ConstructViaDefaultFactory()
    {
        // Construction smoke: the default dependency factory must build every
        // registered handler WITHOUT OverrideDependency. A future handler with an
        // unresolvable concrete dependency fails here naming the handler and the
        // remedy (DispatchHarness throws with instructions). Handlers is the cached
        // types.Select(CreateHandler) list, so asserting against it directly would be
        // tautological; the claim under test is only "construction succeeds for all".
        using var harness = new DispatchHarness();
        Assert.NotEmpty(harness.Handlers);
    }

    [Fact]
    public void FindSongSessionKey_PinnedToWireFormatLiteral()
    {
        // Pins the constant AND the harness literal to the wire-format literal
        // "FindSongSessionData" (the key Alexa echoes back in session attributes; it
        // must never change). The production force-route reader (HandlerSelector, used
        // by the controller and this harness) matches on the constant, so both pins
        // plus the shared selection path make a rename impossible to miss.
        Assert.Equal("FindSongSessionData", FindSongIntentHandler.SessionDataKey);
        Assert.Equal("FindSongSessionData", DispatchHarness.FindSongSessionKey);
    }

    // Every intent name constant has exactly one claiming handler, and the harness
    // selects a claimer. IntentNames covers all custom intent constants plus the
    // AMAZON.* built-ins (except ProactiveSubscriptionChanged, a request type).
    [Fact]
    public void EveryNamedIntent_HasExactlyOneOwner_AndHarnessSelectsIt()
    {
        using var harness = new DispatchHarness();

        // No intent is currently multi-owned; a new entry here means two handlers'
        // CanHandle claim the same intent name and registration order silently picks.
        var multiOwnerAllowlist = new HashSet<string>(StringComparer.Ordinal);

        foreach (string intentName in EnumerateIntentNameConstants())
        {
            IntentRequest request = IntentProbe(intentName);
            List<BaseHandler> claimers = harness.Handlers.Where(h => h.CanHandle(request)).ToList();
            RoutingDecision decision = harness.Select(DispatchHarness.CreateSkillRequest(request));

            Assert.True(claimers.Count >= 1,
                $"No handler claims intent '{intentName}'");

            Assert.True(claimers.Contains(decision.Handler),
                $"Harness did not select a claimer for intent '{intentName}' " +
                $"(selected: {decision.Handler?.GetType().Name ?? "none"})");

            if (claimers.Count > 1)
            {
                Assert.True(multiOwnerAllowlist.Contains(intentName),
                    $"Intent '{intentName}' claimed by [{string.Join(", ", claimers.Select(c => c.GetType().Name))}] " +
                    $"but is not in the multi-owner allowlist");
            }
        }
    }

    // Every CUSTOM intent declared in ANY embedded interaction model has a registered
    // handler owner. Catches model drift: an intent in a model with no handler routes
    // to the controller's CouldNotUnderstand tell at runtime. No allowlist: a custom
    // intent this skill publishes must be answerable (the JF-450 loop intents were the
    // last exceptions and are now claimed by the loop handlers).
    [Fact]
    public void EveryModelCustomIntent_HasARegisteredOwner()
    {
        using var harness = new DispatchHarness();

        foreach ((string locale, string intentName) in ModelIntents)
        {
            if (intentName.StartsWith("AMAZON.", StringComparison.Ordinal))
            {
                continue; // Built-ins may be unhandled by design.
            }

            bool claimed = harness.Handlers.Any(h => h.CanHandle(IntentProbe(intentName)));
            Assert.True(claimed,
                $"Locale {locale} declares intent '{intentName}' but no handler claims it");
        }
    }

    // Reverse check: every handler-claimed intent appears in at least one embedded
    // model (custom and AMAZON built-ins alike). Catches handlers that claim intent
    // names the models never declare, which Amazon therefore never routes to the skill.
    [Fact]
    public void EveryClaimedIntent_AppearsInSomeModel()
    {
        var modelIntentSet = new HashSet<string>(
            ModelIntents.Select(m => m.IntentName),
            StringComparer.Ordinal);

        // The one documented exception: PlayIntent's handler stays reachable via the
        // hardware PlaybackController Play button, but the intent NAME is declared in
        // no model (JF-451). SetReminderIntent is declared in all 17 models since
        // JF-451; AMAZON.RepeatIntent's handler was deleted (now-playing is
        // MediaInfoIntent's job).
        var notInModelAllowlist = new HashSet<string>(
            new[] { "PlayIntent" }, StringComparer.Ordinal);

        var claimed = EnumerateIntentNameConstants()
            .ToHashSet(StringComparer.Ordinal);

        var missing = claimed
            .Where(n => !notInModelAllowlist.Contains(n) && !modelIntentSet.Contains(n))
            .ToList();
        Assert.True(missing.Count == 0,
            "Handler-claimed intents not declared in any model: " + string.Join(", ", missing));

        // Staleness guard: an allowlisted intent that a model now declares must be
        // removed.
        var stale = notInModelAllowlist
            .Where(n => modelIntentSet.Contains(n))
            .ToList();
        Assert.True(stale.Count == 0,
            "These allowlisted intents are now declared in a model; " +
            "remove them from notInModelAllowlist: " + string.Join(", ", stale));
    }

    // Dead-code detector: every REGISTERED handler fires CanHandle for at least one
    // constructed probe request. A handler that cannot fire is unreachable code (the
    // FallbackIntentHandler incident class).
    [Fact]
    public void EveryRegisteredHandler_FiresForAtLeastOneProbeRequest()
    {
        using var harness = new DispatchHarness();

        var deadHandlers = new HashSet<BaseHandler>(harness.Handlers);
        foreach (Request probe in BuildAllProbes())
        {
            foreach (BaseHandler handler in harness.Handlers)
            {
                if (handler.CanHandle(probe))
                {
                    deadHandlers.Remove(handler);
                }
            }
        }

        Assert.True(deadHandlers.Count == 0,
            "Handlers that never fire for any probe (dead code; if intentional, give " +
            "the handler a probe in BuildAllProbes and document why): " +
            string.Join(", ", deadHandlers.Select(h => h.GetType().Name)));
    }

    // An IntentRequest with FindSongSessionData force-routes to FindSongIntentHandler
    // even when the NLU intent name would otherwise route elsewhere (short replies like
    // "family" get misrouted to ShowMoreIntent while a FindSong dialog is open).
    [Fact]
    public void FindSongSession_ActiveIntentRequest_ForceRoutesToFindSong()
    {
        using var harness = new DispatchHarness();

        var skillRequest = DispatchHarness.CreateSkillRequest(
            IntentProbe(IntentNames.ShowMore), // Would route to ShowMoreIntentHandler
            sessionAttributes: SessionWithFindSongData());

        RoutingDecision decision = harness.Select(skillRequest);

        Assert.IsType<FindSongIntentHandler>(decision.Handler);
        Assert.True(decision.ForceRouted);
    }

    // The 2026-08-21 incident mirror: a SessionEndedRequest carrying FindSong
    // attributes must fall through to SessionEndedRequestHandler. The pre-fix
    // controller force-routed it to FindSongIntentHandler, whose HandleAsync cast the
    // request to IntentRequest and crashed with InvalidCastException.
    [Fact]
    public async Task FindSongSession_SessionEndedRequest_FallsThroughToSessionEndedHandler_NoCast()
    {
        using var harness = new DispatchHarness();
        harness.EnableExecution();

        var skillRequest = DispatchHarness.CreateSkillRequest(
            new SessionEndedRequest(),
            sessionAttributes: SessionWithFindSongData());

        RoutingDecision decision = await harness.DispatchAsync(skillRequest);

        Assert.IsType<SessionEndedRequestHandler>(decision.Handler);
        Assert.False(decision.ForceRouted);
        Assert.NotNull(decision.Response); // No InvalidCastException.
    }

    // An unknown intent name selects no handler; the controller answers with the
    // CouldNotUnderstand tell. FallbackIntentHandler does NOT claim arbitrary intent
    // names (only AMAZON.FallbackIntent), so NLU-level fallbacks are its only input.
    [Fact]
    public void UnknownIntentName_SelectsNoHandler()
    {
        using var harness = new DispatchHarness();

        RoutingDecision decision = harness.Select(
            DispatchHarness.CreateSkillRequest(IntentProbe("NotARealIntent")));

        Assert.Null(decision.Handler);
        Assert.False(decision.ForceRouted);
    }

    // The one documented CanHandle tie: the hardware PlaybackController Play button is
    // claimed by BOTH PlayIntentHandler and ResumeIntentHandler. Alphabetical
    // registration order makes PlayIntentHandler win; renaming either class flips the
    // winner, which this test forces into conscious review.
    [Fact]
    public void HardwarePlayButton_RoutesToPlayIntentHandler_DespiteResumeTie()
    {
        using var harness = new DispatchHarness();
        PlaybackControllerRequest request = TestHelpers.CreatePlayCommand();

        List<BaseHandler> claimers = harness.Handlers.Where(h => h.CanHandle(request)).ToList();
        Assert.Contains(claimers, h => h is PlayIntentHandler);
        Assert.Contains(claimers, h => h is ResumeIntentHandler);

        RoutingDecision decision = harness.Select(DispatchHarness.CreateSkillRequest(request));
        Assert.IsType<PlayIntentHandler>(decision.Handler);
        Assert.False(decision.ForceRouted);
    }

    // The JF-419 shape through the full harness path: a warming song index makes a
    // dispatched FindSong request answer with the session-ending SkillWarmingUp tell
    // (the RequestPipeline owns the translation; see its SkillWarmingUpException catch).
    [Fact]
    public async Task WarmingSongIndex_FindSongDispatch_ProducesWarmingTell()
    {
        using var harness = new DispatchHarness();

        // The index is present but still loading (IsReady=false, IsDisabled=false):
        // the exact state IndexWarmingGate.EnsureReady refuses.
        harness.OverrideDependency(Mock.Of<ISongNgramIndex>(
            i => i.IsReady == false && i.IsDisabled == false));

        Entities.User user = harness.EnableExecution();

        var skillRequest = DispatchHarness.CreateSkillRequest(
            IntentProbe(IntentNames.FindSongIntent),
            context: DispatchHarness.CreateExecutionContext(user));

        RoutingDecision decision = await harness.DispatchAsync(skillRequest);

        Assert.IsType<FindSongIntentHandler>(decision.Handler);
        Assert.NotNull(decision.Response);
        Assert.True(decision.Response.Response.ShouldEndSession);

        string speech = TestHelpers.GetSpeechText(decision.Response);
        Assert.Contains(ResponseStrings.Get("SkillWarmingUp", "en-US"), speech, StringComparison.OrdinalIgnoreCase);
    }

    private static IntentRequest IntentProbe(string intentName)
        => new()
        {
            Intent = new Intent { Name = intentName },
            Locale = "en-US",
            RequestId = "test-req"
        };

    private static Dictionary<string, object> SessionWithFindSongData()
        => new() { [DispatchHarness.FindSongSessionKey] = "{}" };

    /// <summary>
    /// Enumerates all string constants declared on IntentNames (intent names and
    /// AMAZON.* built-ins), excluding ProactiveSubscriptionChanged (a request type,
    /// not an intent name). Delegates to the production-side reflection property so
    /// the enumeration logic exists once (the simulator's known-intent list reads
    /// the same source).
    /// </summary>
    private static IEnumerable<string> EnumerateIntentNameConstants()
        => IntentNames.AllIntentNames;

    /// <summary>
    /// (locale, intent name) for every intent declared in every embedded locale
    /// interaction model, via the same manifest enumeration production uses.
    /// Memoized once per test-assembly run: the embedded models are immutable at
    /// test runtime, and each full pass parses all 17 JSONs (~722KB of
    /// LINQ-to-JSON DOM), which three consumers were each repeating.
    /// </summary>
    private static readonly IReadOnlyList<(string Locale, string IntentName)> ModelIntents = LoadModelIntents();

    private static IReadOnlyList<(string Locale, string IntentName)> LoadModelIntents()
    {
        var results = new List<(string Locale, string IntentName)>();
        var assembly = typeof(global::Jellyfin.Plugin.AlexaSkill.Util).Assembly;

        foreach (Tuple<string, string> model in global::Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels())
        {
            using Stream? stream = assembly.GetManifestResourceStream(model.Item2);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var root = JObject.Parse(reader.ReadToEnd());

            foreach (JToken? intent in root["languageModel"]?["intents"] ?? Enumerable.Empty<JToken>())
            {
                string? name = intent?["name"]?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    results.Add((model.Item1, name!));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Builds the probe set covering every request shape the handler ecosystem must
    /// handle: all IntentNames constants, every intent declared in any embedded model
    /// (both directions of model drift get probed), and every non-intent request type.
    /// Extend this when a new request shape appears; the dead-code detector fails with
    /// the handler name until a probe covers it.
    /// </summary>
    private static List<Request> BuildAllProbes()
    {
        var probes = new List<Request>();

        var intentNames = EnumerateIntentNameConstants()
            .Concat(ModelIntents.Select(m => m.IntentName))
            .ToHashSet(StringComparer.Ordinal);

        foreach (string intentName in intentNames)
        {
            probes.Add(IntentProbe(intentName));
        }

        // LaunchRequest (no Task) → LaunchRequestHandler.
        probes.Add(new LaunchRequest { Locale = "en-US" });

        // LaunchRequest with a Task → SkillConnectionHandler.
        probes.Add(new LaunchRequest
        {
            Locale = "en-US",
            Task = new LaunchRequestTask { Name = "PlayFavorites" }
        });

        // SessionEndedRequest → SessionEndedRequestHandler.
        probes.Add(new SessionEndedRequest());

        // SystemExceptionRequest → ExceptionHandler.
        probes.Add(new SystemExceptionRequest());

        // AplUserEventRequest → AplUserEventHandler.
        probes.Add(new AplUserEventRequest { Arguments = new JArray() });

        // AudioPlayer events.
        foreach (string audioType in new[]
        {
            "AudioPlayer.PlaybackStarted",
            "AudioPlayer.PlaybackFinished",
            "AudioPlayer.PlaybackNearlyFinished",
            "AudioPlayer.PlaybackStopped",
            "AudioPlayer.PlaybackFailed"
        })
        {
            probes.Add(new AudioPlayerRequest { Type = audioType });
        }

        // PlaybackController hardware Play button (the tie case).
        probes.Add(TestHelpers.CreatePlayCommand());

        // ProactiveSubscriptionChanged → ProactiveSubscriptionChangedHandler.
        probes.Add(new ProactiveSubscriptionChangedProbeRequest());

        return probes;
    }

    /// <summary>
    /// Probe request for ProactiveSubscriptionChangedHandler: Request.Type is settable
    /// on the base class and the handler matches the type string.
    /// </summary>
    private sealed class ProactiveSubscriptionChangedProbeRequest : Request
    {
        public ProactiveSubscriptionChangedProbeRequest()
        {
            Type = IntentNames.ProactiveSubscriptionChanged;
        }
    }
}

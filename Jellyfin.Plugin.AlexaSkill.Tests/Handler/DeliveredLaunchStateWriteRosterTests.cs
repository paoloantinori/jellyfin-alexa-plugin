using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-718 item 4: the delivered-launch STATE-WRITE roster, the twin of
/// <see cref="DeliveredLaunchOutputSpeechRosterTests"/> (same
/// <see cref="IlCallScanner"/> idiom) for the phantom now-playing class the JF-714
/// sweep gated handler-side. The invariant: every plugin method that BOTH calls a
/// VideoApp-family launch builder that can answer a NON-LAUNCH response (the
/// VideoRequiresScreen capability Tell, the channel builder's resolver-null Tell)
/// AND writes <see cref="SessionInfo.NowPlayingQueue"/> or
/// <see cref="SessionInfo.FullNowPlayingItem"/> in that same method must route the
/// write through the delivered-launch gate family (a call to
/// <see cref="PlaybackLaunchBuilder.HasLaunchDirective"/> or
/// <see cref="PlaybackLaunchBuilder.AttachNowPlayingIfLaunched"/>), directly or via
/// a one-level same-type helper. Writing now-playing for a launch that will not
/// happen is the phantom: a later "what's playing"/next/previous answers an item
/// that never started. <see cref="PlaybackLaunchBuilder.HasVideoAppLaunchDirective"/>
/// is deliberately NOT an acceptable gate here: it is the ROUTE verdict
/// (VideoApp-only), and the episode screenless degrade delivers an AudioPlayer.Play
/// whose now-playing writes must survive.
/// SCOPE (deliberate, the JF-718 filing shape): only the VideoApp-Tell-capable
/// builders are in the conjunction. The AudioPlayer family
/// (<see cref="PlaybackLaunchBuilder.BuildAudioPlayerResponse"/> and the
/// audio-degrading builders) is throw-or-launch since JF-699 item 1, so its many
/// post-build ungated writes are safe-by-construction TODAY; extending this roster
/// to that family is the filed belt question (JF-732), not this one.
/// ACCEPTED BOUNDARIES (the roster idiom's documented-limit class):
/// 1. WRITE-DETECTION DEPTH: the state WRITE is detected in the scanned method's
///    own IL only, while the builder call and the gate may sit in a one-level
///    same-type helper. A write delegated to a helper on the same type escapes
///    the conjunction when that helper itself calls no Tell-capable builder (the
///    PlayRadio shape: HandleAsync launches channels, its StartRadioPlayback
///    helper writes state around the AudioPlayer builder).
/// 2. GATE-SIDE LOOSENESS: the gate verdict keys on ANY reference to the gate
///    family in the method or its helpers, not on the write itself being gated.
///    A method that references the family for a DIFFERENT purpose (ResumeIntent
///    and StartOver gate speech/position clears with HasLaunchDirective;
///    BaseHandler's fuzzy-miss block gates the qualifier send) satisfies the
///    check even with a raw ungated state write added beside the builder call,
///    and a same-type helper's legitimate gate call masks a raw write in the
///    calling method the same way.
/// 3. WRITE-SHAPE BLIND SPOT: the write probe covers only the two session
///    fields, so a handler-side DeviceQueueManager.RecordLastPlayed beside a
///    Tell-capable builder (the channel builder's exact pre-JF-718 shape)
///    escapes this pin entirely. Latent-only today (the two non-builder
///    RecordLastPlayed sites, SleepTimer's re-issue and the pipeline
///    interceptor, call no Tell-capable builder); if JF-732's family belt
///    lands, the RecordLastPlayed probe belongs on that roster.
/// A miss under any boundary cannot silently pass a gated site (the scan only
/// adds candidates), it can only miss an ungated one, and the next same-method
/// change re-surfaces it.
/// SELF-RED: removing the gate reference from a flagged method flips this test by
/// itself (proven red against the pre-JF-718 tree, which flagged exactly the
/// three then-ungated sites); a new builder+write site fails until its author
/// gates (or documents) it.
/// </summary>
public class DeliveredLaunchStateWriteRosterTests
{
    /// <summary>
    /// The launch builders that can answer a response with NO launch directive:
    /// the four VideoApp-family members with a capability/resolver Tell arm. The
    /// audio-degrading builders (BuildVideoAppAudioResponse,
    /// BuildAudiobookResumeResponse, BuildAudiobookVideoAppLaunchResponseAsync)
    /// and <see cref="PlaybackLaunchBuilder.BuildAudioPlayerResponse"/> always
    /// deliver exactly one directive or throw, so they are out of the conjunction.
    /// </summary>
    private static readonly string[] BuilderMethodNames =
    {
        nameof(PlaybackLaunchBuilder.BuildVideoAppLaunchResponse),
        nameof(PlaybackLaunchBuilder.BuildVideoAppLaunchResponseAsync),
        nameof(PlaybackLaunchBuilder.BuildEpisodeLaunchResponseAsync),
        nameof(PlaybackLaunchBuilder.BuildChannelLaunchResponseAsync),
    };

    /// <summary>
    /// The accepted delivered-launch gates for a state write.
    /// <see cref="PlaybackLaunchBuilder.HasVideoAppLaunchDirective"/> is
    /// deliberately absent (route verdict, not delivered verdict).
    /// </summary>
    private static readonly string[] GateMethodNames =
    {
        nameof(PlaybackLaunchBuilder.HasLaunchDirective),
        nameof(PlaybackLaunchBuilder.AttachNowPlayingIfLaunched),
    };

    /// <summary>
    /// Methods allowed to write now-playing state after a Tell-capable builder
    /// call WITHOUT a gate reference, each with its documented reason. Kept EMPTY
    /// by policy (the speech-roster twin's rule): a site safe by construction
    /// still gets the belt, because a certification rots the moment a builder
    /// learns a new non-launch return, while a runtime gate cannot.
    /// </summary>
    private static readonly HashSet<string> Allowlist = new();

    [Fact]
    public void PostBuilderNowPlayingWrites_RideADeliveredLaunchGate()
    {
        Module pluginModule = typeof(BaseHandler).Module;

        HashSet<int> builderTokens = new(
            BuilderMethodNames.SelectMany(name => IlCallScanner.MethodTokens(typeof(PlaybackLaunchBuilder), name)));
        HashSet<int> gateTokens = new(
            GateMethodNames.SelectMany(name => IlCallScanner.MethodTokens(typeof(PlaybackLaunchBuilder), name)));

        var failures = new List<string>();
        foreach ((Type type, MethodBase method) in IlCallScanner.DeclaredMethods(pluginModule.Assembly))
        {
            // The builder writes session state itself (the channel launch); its own
            // methods are the machinery, not post-builder consumers.
            Type owner = IlCallScanner.TopLevelType(method.DeclaringType ?? type);
            if (owner == typeof(PlaybackLaunchBuilder))
            {
                continue;
            }

            // The write probe first: by far the most selective leg (only a handful
            // of methods in the assembly write the setters), so the costlier
            // builder/helper walks run only for actual write sites.
            if (!WritesNowPlayingState(method, pluginModule))
            {
                continue;
            }

            if (!IlCallScanner.CallsDirectlyOrViaSameTypeHelper(method, pluginModule, builderTokens))
            {
                continue;
            }

            // The state write is detected in the method's OWN IL (the documented
            // boundary above): the phantom shape is a write sitting beside the
            // builder call in the same method, which is the house pattern every
            // JF-714/JF-718 site follows.
            string site = $"{owner.Name}.{IlCallScanner.LogicalMethodName(method)}";
            if (IlCallScanner.CallsDirectlyOrViaSameTypeHelper(method, pluginModule, gateTokens)
                || Allowlist.Contains(site))
            {
                continue;
            }

            failures.Add(
                $"{site}: calls a Tell-capable VideoApp-family launch builder and writes NowPlayingQueue/FullNowPlayingItem " +
                "in the same method with NO delivered-launch gate (HasLaunchDirective / AttachNowPlayingIfLaunched). " +
                "Route the write through PlaybackLaunchBuilder.AttachNowPlayingIfLaunched (or gate the block on HasLaunchDirective), " +
                "so a capability/resolver Tell can never leave phantom now-playing state (the JF-714/JF-718 class).");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// True when the method's IL assigns <see cref="SessionInfo.NowPlayingQueue"/>
    /// or <see cref="SessionInfo.FullNowPlayingItem"/> (the setter twins of the
    /// speech roster's <c>set_OutputSpeech</c> probe; the setters are memberrefs
    /// into the Jellyfin assembly, resolved through the module).
    /// </summary>
    private static bool WritesNowPlayingState(MethodBase method, Module module)
        => IlCallScanner.CallsNamedMethod(method, module, "set_NowPlayingQueue", typeof(SessionInfo))
        || IlCallScanner.CallsNamedMethod(method, module, "set_FullNowPlayingItem", typeof(SessionInfo));
}

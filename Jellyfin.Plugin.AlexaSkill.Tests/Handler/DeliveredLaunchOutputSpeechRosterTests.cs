using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-699 item 3(b) + item 6: the delivered-launch output-speech roster as a TEST
/// (the WarmingGateCoverageTests / AudioPlayerPlayConstructionRosterTests idiom).
/// The invariant: every plugin method that BOTH calls a launch-builder family
/// member AND writes <see cref="ResponseBody.OutputSpeech"/> must route the write
/// through a delivered-launch gate (a call to
/// <see cref="PlaybackLaunchBuilder.HasLaunchDirective"/>,
/// <see cref="PlaybackLaunchBuilder.HasVideoAppLaunchDirective"/>, or either
/// <see cref="PlaybackLaunchBuilder.AttachAnnounceIfLaunched"/> overload),
/// directly or via a one-level same-type helper. POST-JF-699 ITEM 1 the builder
/// families throw-or-launch (a refusal throws
/// StreamTokenNotConfiguredException; the AudioPlayer/VideoAppAudio/
/// AudiobookResume builders always emit exactly one launch directive otherwise),
/// so the gates are BELT, not load-bearing refusal protection: they guard a
/// FUTURE builder return that is not a launch (e.g. a capability Tell added
/// inside the AudioPlayer chokepoint). The JF-693-era verdict wrappers were
/// deleted as tautologies; this roster is what keeps the belt on every site, so
/// the next handler cannot reintroduce the JF-693 overwrite class silently (the
/// item 6 sites FollowMe/Recommend/PlayRadio were the known ungated ones; their
/// writes are gated since JF-699). Ground truth is the plugin assembly's IL
/// (the shared <see cref="IlCallScanner"/); methods that write speech only onto
/// responses they built THEMSELVES (ResponseBuilder.Tell / object initializers)
/// never call a launch builder and are out of scope by construction. The scan
/// walks each method's IL ONCE: the call tokens are snapshotted, the same-type
/// helpers resolved once, and the three verdicts (builder call, speech write,
/// gate reference) derived from that snapshot.
/// ACCEPTED BOUNDARY (the roster idiom's documented-limit class): helper
/// discovery is ONE LEVEL and SAME-TOP-LEVEL-TYPE, so a speech write delegated
/// to a helper on ANOTHER type escapes the conjunction; a miss here cannot
/// silently pass a gated site (the scan only adds candidates), it can only miss
/// an ungated one, and the next same-type change re-surfaces it.
/// SELF-RED: removing a gate reference from a flagged method flips this test by
/// itself; a new builder+write site fails until its author gates (or documents)
/// it. Item 3(a) (AudioPlayerPlayDirective construction only in the builder +
/// the guarded sleep re-issue) is owned by
/// <see cref="AudioPlayerPlayConstructionRosterTests"/> since JF-631 and is NOT
/// reimplemented here.
/// </summary>
public class DeliveredLaunchOutputSpeechRosterTests
{
    /// <summary>
    /// The launch-builder family whose responses a handler may want to speak over:
    /// every PlaybackLaunchBuilder member that BUILDS a launch response (the guard
    /// and the URL/resolver members are not), plus the two CrossMedia play shapes
    /// that wrap the AudioPlayer chokepoint.
    /// </summary>
    private static readonly string[] BuilderMethodNames =
    {
        nameof(PlaybackLaunchBuilder.BuildAudioPlayerResponse),
        nameof(PlaybackLaunchBuilder.BuildVideoAppLaunchResponse),
        nameof(PlaybackLaunchBuilder.BuildVideoAppLaunchResponseAsync),
        nameof(PlaybackLaunchBuilder.BuildVideoAppAudioResponse),
        nameof(PlaybackLaunchBuilder.BuildAudiobookResumeResponse),
        nameof(PlaybackLaunchBuilder.BuildAudiobookVideoAppLaunchResponseAsync),
        nameof(PlaybackLaunchBuilder.BuildEpisodeLaunchResponseAsync),
        nameof(PlaybackLaunchBuilder.BuildChannelLaunchResponseAsync),
        nameof(CrossMediaFallback.BuildSingleSongResponse),
        nameof(CrossMediaFallback.BuildArtistSongsResponseAsync),
    };

    /// <summary>
    /// The accepted delivered-launch gates (JF-699 item 4 folds the VideoApp sniff
    /// into the family): a speech write whose method references any of these passes.
    /// </summary>
    private static readonly string[] GateMethodNames =
    {
        nameof(PlaybackLaunchBuilder.HasLaunchDirective),
        nameof(PlaybackLaunchBuilder.HasVideoAppLaunchDirective),
        nameof(PlaybackLaunchBuilder.AttachAnnounceIfLaunched),
    };

    /// <summary>
    /// Methods allowed to write speech after a builder call WITHOUT a gate
    /// reference, each with its documented reason. Kept EMPTY by policy: a site
    /// that is safe-by-construction (JF-699 throw-or-launch) still gets the belt,
    /// because a certification here would rot the moment a builder learns a new
    /// non-launch return, while a runtime gate cannot.
    /// </summary>
    private static readonly HashSet<string> Allowlist = new();

    [Fact]
    public void PostBuilderOutputSpeechWrites_RideADeliveredLaunchGate()
    {
        Module pluginModule = typeof(BaseHandler).Module;

        HashSet<int> builderTokens = new(
            BuilderMethodNames.SelectMany(Name => Name switch
            {
                nameof(CrossMediaFallback.BuildSingleSongResponse) or nameof(CrossMediaFallback.BuildArtistSongsResponseAsync)
                    => IlCallScanner.MethodTokens(typeof(CrossMediaFallback), Name),
                _ => IlCallScanner.MethodTokens(typeof(PlaybackLaunchBuilder), Name),
            }));

        HashSet<int> gateTokens = new(
            GateMethodNames.SelectMany(Name => IlCallScanner.MethodTokens(typeof(PlaybackLaunchBuilder), Name)));

        List<string> failures = DeliveredLaunchWriteRosterScan.FindUngatedPostBuilderWrites(
            pluginModule,
            builderTokens,
            gateTokens,
            WritesOutputSpeech,
            Allowlist,
            "calls a launch builder and writes ResponseBody.OutputSpeech with NO delivered-launch gate " +
            "(HasLaunchDirective / HasVideoAppLaunchDirective / AttachAnnounceIfLaunched). Route the write through " +
            "PlaybackLaunchBuilder.AttachAnnounceIfLaunched (or gate the block on HasLaunchDirective/HasVideoAppLaunchDirective), " +
            "so a future non-launch builder return can never be spoken over (the JF-693 overwrite class; JF-699 item 3b/6).");

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// True when the method's IL contains a set_OutputSpeech call on
    /// <see cref="ResponseBody"/> (the shared <see cref="IlCallScanner.CallsNamedMethod"/>,
    /// the CallsGetter setter twin).
    /// </summary>
    private static bool WritesOutputSpeech(MethodBase method, Module module)
        => IlCallScanner.CallsNamedMethod(method, module, "set_OutputSpeech", typeof(ResponseBody));
}

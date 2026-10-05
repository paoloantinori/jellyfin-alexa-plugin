using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using global::Alexa.NET;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-758: the launch-pairing belt pinned at both builder entries that accept an
/// independent (itemId, item) pair (the AudioPlayer chokepoint
/// <see cref="PlaybackLaunchBuilder"/>.BuildAudioPlayerResponse and its
/// VideoApp-for-audio sibling BuildVideoAppAudioResponse; the full contract, the
/// three construction idioms that keep it, and the JF-750 drift it hardens
/// against live on the guard's doc,
/// PlaybackLaunchBuilder.EnsureItemPairsWithLaunchId). Two halves: the MISMATCH
/// verdict (a non-null item paired with an id resolving to a DIFFERENT item
/// throws, through the string overload, the AudioLaunchSource overload that
/// funnels into it, and the VideoApp-audio sibling's own branch, with both ids
/// in the message so a first fire is self-diagnosing) and the SILENT SHAPES the
/// design constraints mandate (the deliberate null-item enqueue of
/// PlayIntentHandler; the dashless "N"-format id of the APL carousel tap, which
/// is why the compare is GUID-based and not string-based; composite stream
/// tokens <c>{guid}|launch:n</c> / <c>{guid}|sleep:t</c>, judged by the item
/// the codec resolves them to; a genuinely unparseable unknown-suffix token,
/// where there is no item id to compare). STRUCTURAL: the roster pin below
/// forces every FUTURE builder member accepting the independent pair to carry
/// or delegate the belt (the ThrowOrLaunch family's deep-construct analogue for
/// this contract). SELF-RED: delete a belt call at either entry and both its
/// behavioral mismatch pin and the roster pin flip (the launch returns instead
/// of throwing); weaken the compare to a string equality and the dashless pin
/// flips; revert the codec resolution to a raw Guid parse and the
/// composite-token mismatch pin flips.
/// </summary>
[Collection("Plugin")]
public class PlaybackLaunchBuilderLaunchPairingPinTests : PluginTestBase
{
    private readonly PluginConfiguration _config = new();
    private readonly PlaybackLaunchBuilder _launch;

    public PlaybackLaunchBuilderLaunchPairingPinTests()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _launch = TestHelpers.CreateLaunchBuilder(_config);
    }

    private static Entities.User CreateUser()
        => TestHelpers.CreateTestUser(jellyfinToken: "tok");

    // A static Jellyfin stream URL: no plugin token marker, so the JF-687
    // delivery gate can never fire and the pairing belt is the only throw the
    // mismatch pins can hit.
    private static string StaticUrl(Guid id)
        => $"https://test.example.com/Audio/{id}/stream?static=true&api_key=tok";

    [Fact]
    public void Pairing_MismatchedIdAndItem_ThrowsNamingBothIds()
    {
        var song = TestHelpers.CreateSong("Launched Metadata");
        var other = TestHelpers.CreateSong("Other Track");

        var ex = Assert.Throws<InvalidOperationException>(() => _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, StaticUrl(other.Id), other.Id.ToString(), song, CreateUser(), TestHelpers.CreateTestContext()));

        // Both ids in the message, so the first fire names the drifted pair
        // without a debugger (the task's "descriptive exception" requirement).
        Assert.Contains(other.Id.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains(song.Id.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("JF-758", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pairing_MismatchThroughSourceOverload_Throws()
    {
        // The AudioLaunchSource overload (the JF-522 structural pairing triple)
        // funnels into the same chokepoint, so the belt covers its callers too;
        // pinned separately so a future re-plumbing of that overload around the
        // main one cannot silently drop it.
        var song = TestHelpers.CreateSong("Metadata Item");
        var other = TestHelpers.CreateSong("Stream Item");
        var source = new AudioLaunchSource(StaticUrl(other.Id), 0, 0);

        Assert.Throws<InvalidOperationException>(() => _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, source, other.Id.ToString(), song, CreateUser(), TestHelpers.CreateTestContext()));
    }

    [Fact]
    public void Pairing_MismatchThroughVideoAppAudioBuilder_Throws()
    {
        // The VideoApp-for-audio sibling accepts the same independent pair and is
        // reached DIRECTLY by production callers that never pass the AudioPlayer
        // chokepoint (the audiobook launch composition, Resume's book arm), so a
        // drift there is covered only by this member's own belt call. The
        // VideoApp-capable context keeps the builder on its VideoApp branch, so the
        // throw can only come from that belt, not from a chokepoint re-entry (the
        // screenless degrade's re-entry is pinned by the capability-gate suites).
        var song = TestHelpers.CreateSong("Metadata Item");
        var other = TestHelpers.CreateSong("Stream Item");

        Assert.Throws<InvalidOperationException>(() => _launch.BuildVideoAppAudioResponse(
            other.Id.ToString(), song, CreateUser(), context: TestHelpers.CreateContextWithVideoApp()));
    }

    [Fact]
    public void Pairing_NullItem_StaysSilent()
    {
        // The deliberate null-item contract (PlayIntentHandler enqueues with only
        // the queue id surviving): the belt must skip, and the launch must still
        // deliver the AudioPlayer.Play directive.
        var queued = TestHelpers.CreateSong();

        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.Enqueue, StaticUrl(queued.Id), queued.Id.ToString(), null, CreateUser(), TestHelpers.CreateTestContext());

        var directive = Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Equal(queued.Id.ToString(), directive.AudioItem.Stream.Token);
    }

    [Fact]
    public void Pairing_DashlessNFormatId_StaysSilent()
    {
        // The APL carousel tap shape: the list items are built with
        // Id.ToString("N") (BrowseLibraryIntentHandler), so the tap hands the
        // DASHLESS id up as the launch itemId. A GUID-based compare treats it as
        // the same item; this pin flips the moment anyone "simplifies" the belt
        // to a string equality.
        var tapped = TestHelpers.CreateSong();
        string dashlessId = tapped.Id.ToString("N");
        Assert.NotEqual(dashlessId, tapped.Id.ToString()); // guard the test's own shape

        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, StaticUrl(tapped.Id), dashlessId, tapped, CreateUser(), TestHelpers.CreateTestContext());

        var directive = Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Equal(dashlessId, directive.AudioItem.Stream.Token);
        Assert.Equal(tapped.Name, directive.AudioItem.Metadata?.Title);
    }

    [Fact]
    public void Pairing_CompositeStreamTokenIdNamingSameItem_StaysSilent()
    {
        // The StreamTokenCodec composite forms ({guid}|launch:n, {guid}|sleep:t)
        // resolve to the item they name (the ONE token parser, the same resolution
        // the Resume tail's displaced-token fix follows), so a composite launch
        // whose token names the item being launched passes the pairing and the
        // directive carries the composite token verbatim.
        var song = TestHelpers.CreateSong();
        string compositeToken = song.Id + "|launch:7";

        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, StaticUrl(song.Id), compositeToken, song, CreateUser(), TestHelpers.CreateTestContext());

        var directive = Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Equal(compositeToken, directive.AudioItem.Stream.Token);
    }

    [Fact]
    public void Pairing_CompositeStreamTokenIdNamingOtherItem_Throws()
    {
        // A composite token whose guid part names ANOTHER item than the metadata
        // item is a pairing drift exactly like a bare mismatched id would be; the
        // belt resolves through the codec and throws. This was the shape the
        // original raw-parse belt waved through, the same shape the Resume tail
        // was shipping.
        var song = TestHelpers.CreateSong("Metadata Item");
        var other = TestHelpers.CreateSong("Token Item");

        Assert.Throws<InvalidOperationException>(() => _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, StaticUrl(other.Id), other.Id + "|sleep:12345", song, CreateUser(), TestHelpers.CreateTestContext()));
    }

    [Fact]
    public void Pairing_UnknownSuffixTokenId_StaysSilent()
    {
        // Only a genuinely UNPARSEABLE id skips: an unknown suffix makes the token
        // unparseable by the codec's own rule (an unknown-suffix owner must extend
        // the codec, not ad-hoc parse), and with no item id to compare the belt
        // has nothing to judge.
        var song = TestHelpers.CreateSong();
        var other = TestHelpers.CreateSong();
        string unknownSuffixToken = other.Id + "|other:noise";

        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, StaticUrl(song.Id), unknownSuffixToken, song, CreateUser(), TestHelpers.CreateTestContext());

        Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
    }

    /// <summary>
    /// JF-758 roster, the structural analogue of the ThrowOrLaunch family's deep
    /// construct rule: EVERY LAUNCH member that accepts the independent
    /// (itemId, item) pair must either carry the belt itself or delegate to a
    /// belted member, and must be LISTED in the roster below. The pair shape is a
    /// declared string <c>itemId</c> parameter beside a declared
    /// <c>BaseItem item</c> parameter on a member returning SkillResponse or
    /// Task&lt;SkillResponse&gt;, name-checked so a re-parametrized overload
    /// cannot dodge. The pair-accepting RESOLVER
    /// helpers (ResolveAudioLaunchSource, ResolveResumedAudioLaunch) and the
    /// void-returning attachers are outside the rule BY DESIGN: they take the pair
    /// to RESOLVE it, and their outputs feed the belted builders rather than
    /// delivering a launch. Name-independent discovery means a FUTURE launch
    /// member accepting the pair cannot ship unbelted, and a rename that strands a
    /// roster entry reds (staleness). SELF-RED: delete the sibling's belt call and
    /// this pin fails naming it (the construct discriminator keeps its screenless
    /// re-entry from masking the deletion); grow a new pair-accepting launch member
    /// without the belt and it fails naming that.
    /// </summary>
    [Fact]
    public void EveryPairAcceptingBuilderMember_IsRosteredAndBeltsOrDelegates()
    {
        int beltToken = IlCallScanner.MethodTokens(
            typeof(PlaybackLaunchBuilder), nameof(PlaybackLaunchBuilder.EnsureItemPairsWithLaunchId)).Single();

        // The belted entries: each carries the belt at its own entry. The funnels
        // (the context-less convenience overload, the AudioLaunchSource overload)
        // satisfy the rule through the delegation lane below.
        string[] roster =
        {
            nameof(PlaybackLaunchBuilder.BuildAudioPlayerResponse),
            nameof(PlaybackLaunchBuilder.BuildVideoAppAudioResponse),
        };

        static bool AcceptsPair(MethodBase method)
            => (method is MethodInfo { ReturnType: var rt }
                    && (rt == typeof(SkillResponse) || rt == typeof(System.Threading.Tasks.Task<SkillResponse>)))
                && method.GetParameters().Any(p => p.ParameterType == typeof(string) && p.Name == "itemId")
                && method.GetParameters().Any(p
                    => p.ParameterType == typeof(MediaBrowser.Controller.Entities.BaseItem) && p.Name == "item");

        var allMethods = new List<MethodBase>();
        var pairAccepting = new List<MethodBase>();
        foreach ((Type type, MethodBase method) in IlCallScanner.DeclaredMethods(typeof(PlaybackLaunchBuilder).Module.Assembly))
        {
            if (IlCallScanner.TopLevelType(method.DeclaringType ?? type) != typeof(PlaybackLaunchBuilder))
            {
                continue;
            }

            allMethods.Add(method);
            if (AcceptsPair(method))
            {
                pairAccepting.Add(method);
            }
        }

        var failures = new List<string>();

        // STALENESS: a roster entry that no longer discovers as pair-accepting means
        // its signature dropped the pair shape (the member escaped the walk entirely).
        foreach (string member in roster)
        {
            if (!pairAccepting.Any(m => IlCallScanner.LogicalMethodName(m) == member))
            {
                failures.Add($"{member}: roster entry with no declared pair-accepting method (stale after a signature change?)");
            }
        }

        // THE BELT-OR-DELEGATE RULE, name-independent: every discovered
        // pair-accepting method must be rostered AND either carry the belt (the
        // members that CONSTRUCT a response themselves, so the verdict must run at
        // their own entry; the construct discriminator is why the sibling's
        // screenless re-entry into the chokepoint cannot mask a deleted belt call
        // there) or delegate to a belted member (the funnel lane, which never
        // constructs). The async kickoff stub of a pair-accepting async member is
        // exempt exactly like the ThrowOrLaunch family's: its body lives in the
        // MoveNext sibling, which the walk covers through its own shape check.
        var module = typeof(PlaybackLaunchBuilder).Module;
        var rosterTokens = new List<int>();
        foreach (string member in roster)
        {
            rosterTokens.AddRange(IlCallScanner.MethodTokens(typeof(PlaybackLaunchBuilder), member));
        }

        foreach (MethodBase method in pairAccepting)
        {
            string logical = IlCallScanner.LogicalMethodName(method);

            // The async kickoff stub exemption FIRST (the exemption is keyed on the
            // member's own logical name, rostered or not): its body lives in the
            // MoveNext sibling, which the walk covers through its own shape check.
            bool memberHasStateMachine = allMethods.Any(
                m => IlCallScanner.LogicalMethodName(m) == logical && m.Name == "MoveNext");
            bool isAsyncKickoffStub = memberHasStateMachine
                && method is MethodInfo { ReturnType: var stubReturn }
                && stubReturn == typeof(System.Threading.Tasks.Task<SkillResponse>);
            if (isAsyncKickoffStub)
            {
                continue;
            }

            if (!roster.Contains(logical))
            {
                failures.Add($"{logical} ({method.Name}): unrostered pair-accepting (itemId, item) member");
                continue;
            }

            bool satisfies = IlCallScanner.ConstructsType(method, module, typeof(SkillResponse))
                ? IlCallScanner.ContainsCallToToken(method, beltToken)
                : IlCallScanner.ContainsCallToAnyToken(method, rosterTokens);
            if (!satisfies)
            {
                failures.Add($"{logical} ({method.Name}): pair-accepting member carries no belt and delegates to none");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"launch-pairing belt coverage failures: {string.Join("; ", failures)}");
    }
}

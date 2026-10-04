using System;
using global::Alexa.NET;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
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
/// verdict (a non-null item paired with a resolvable id naming a DIFFERENT item
/// throws, through the string overload, the AudioLaunchSource overload that
/// funnels into it, and the VideoApp-audio sibling's own branch, with both ids
/// in the message so a first fire is self-diagnosing) and the THREE SILENT
/// SHAPES the design constraints mandate (the deliberate null-item enqueue of
/// PlayIntentHandler; the dashless "N"-format id of the APL carousel tap, which
/// is why the compare is GUID-based and not string-based; the composite stream
/// token <c>{guid}|launch:n</c> of StreamTokenCodec, which the belt cannot
/// resolve and so must not judge). SELF-RED: delete a belt call at either entry
/// and its mismatch pin flips (the launch returns instead of throwing); weaken
/// the compare to a string equality and the dashless pin flips.
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
    public void Pairing_CompositeStreamTokenId_StaysSilent()
    {
        // The StreamTokenCodec composite forms ({guid}|launch:n, {guid}|sleep:t)
        // do not parse as a Guid, and the belt guards the pairing of RESOLVABLE
        // ids only: even a composite id whose guid part names another item must
        // pass silently (a relaunch that carries the token of the displaced
        // stream is a legitimate shape, not a pairing drift).
        var song = TestHelpers.CreateSong();
        var displaced = TestHelpers.CreateSong();
        string compositeToken = displaced.Id + "|launch:7";

        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, StaticUrl(song.Id), compositeToken, song, CreateUser(), TestHelpers.CreateTestContext());

        Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
    }
}

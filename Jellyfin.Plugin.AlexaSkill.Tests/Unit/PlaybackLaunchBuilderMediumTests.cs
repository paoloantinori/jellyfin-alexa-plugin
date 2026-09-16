using System;
using global::Alexa.NET.Request;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-315 batch-5 characterization suite for the VideoApp MEDIUM family
/// (ResolvePlayingMedium + IsVideoAppMedium + BuildVideoAppTransportRefusal +
/// IsVideoAppLaunchItem + IsActivelyPlaying), which had ZERO direct coverage
/// before the batch (no Pause/Next/Previous handler suites exist; the transport
/// twins are only exercised through these members). Written BEFORE the extraction
/// and run green on the pre-refactor BaseHandler code via a probe subclass, then
/// retargeted to PlaybackLaunchBuilder with receiver swaps only (expectations
/// unchanged; the /simplify gate then replaced the probe with direct internal
/// calls, the members being InternalsVisibleTo-reachable). They pin CURRENT
/// behavior: the ledger-driven medium classification order (empty ledger / token
/// ownership / item kind), the per-medium transport-refusal answers, the ONE
/// VideoApp kind predicate, and the PLAYING/BUFFER_UNDERRUN activity reading.
/// The medium names are pinned as STRINGS: the enum member names are the
/// classification contract the transport handlers switch on.
/// </summary>
[Collection("Plugin")]
public class PlaybackLaunchBuilderMediumTests : PluginTestBase
{
    private readonly PluginConfiguration _config = new();
    private readonly PlaybackLaunchBuilder _builder;

    public PlaybackLaunchBuilderMediumTests()
    {
        // The delegate stands in for BaseHandler.SendProgressiveResponse (the JF-501
        // virtual seam); no member this suite exercises sends a progressive response.
        _builder = TestHelpers.CreateLaunchBuilder(_config);
    }

    /// <summary>
    /// A queue manager with the given item recorded as the device's last play,
    /// plus a library manager resolving that same id to the item (the ledger
    /// read + item resolve pair ResolvePlayingMedium performs).
    /// </summary>
    private static (Mock<ILibraryManager> Library, DeviceQueueManager Queue) LedgerWith(BaseItem item, string deviceId = "test-device")
    {
        DeviceQueueManager queue = TestHelpers.CreateDeviceQueueManager("medium-probe");
        queue.RecordLastPlayed(deviceId, item.Id.ToString());
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        return (library, queue);
    }

    // ---- ResolvePlayingMedium: classification order ----

    [Fact]
    public void Medium_NullLibraryManager_YieldsUnknown()
    {
        Assert.Equal("Unknown", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), null).ToString());
    }

    [Fact]
    public void Medium_EmptyLedger_YieldsUnknown()
    {
        using DeviceQueueManager queue = TestHelpers.CreateDeviceQueueManager("medium-probe");

        Assert.Equal("Unknown", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), Mock.Of<ILibraryManager>(), queue).ToString());
    }

    [Fact]
    public void Medium_LedgerMovie_WithoutMatchingToken_YieldsVideoAndNavigateRefusal()
    {
        var movie = new Movie { Name = "The Matrix", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(movie);

        Assert.Equal("Video", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue).ToString());

        SkillResponse? refusal = PlaybackLaunchBuilder.BuildVideoAppTransportRefusal(_builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue), "en-US");
        Assert.NotNull(refusal);
        Assert.True(refusal.Response.ShouldEndSession, "the navigate refusal is a Tell");
        var speech = Assert.IsType<PlainTextOutputSpeech>(refusal.Response.OutputSpeech);
        Assert.Equal(ResponseStrings.Get("CannotNavigateVideoByVoice", "en-US"), speech.Text);
    }

    [Fact]
    public void Medium_LedgerLiveTvChannel_YieldsLiveTvAndNavigateRefusal()
    {
        var channel = new MediaBrowser.Controller.LiveTv.LiveTvChannel { Name = "CNN", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(channel);

        Assert.Equal("LiveTv", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue).ToString());

        SkillResponse? refusal = PlaybackLaunchBuilder.BuildVideoAppTransportRefusal(_builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue), "en-US");
        Assert.NotNull(refusal);
        var speech = Assert.IsType<PlainTextOutputSpeech>(refusal.Response.OutputSpeech);
        Assert.Equal(ResponseStrings.Get("CannotNavigateLiveTvByVoice", "en-US"), speech.Text);
    }

    [Fact]
    public void Medium_LedgerAudioBook_YieldsVideoAppAudiobookAndSilentRefusal()
    {
        var book = new MediaBrowser.Controller.Entities.AudioBook { Name = "Book", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(book);

        Assert.Equal("VideoAppAudiobook", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue).ToString());

        SkillResponse? refusal = PlaybackLaunchBuilder.BuildVideoAppTransportRefusal(_builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue), "en-US");
        Assert.NotNull(refusal);
        Assert.Null(refusal.Response.OutputSpeech);
        Assert.True(refusal.Response.Directives is not { Count: > 0 }, "the VideoApp book refusal is a silent Empty with no directive");
        Assert.True(refusal.Response.ShouldEndSession, "the VideoApp book refusal is the silent Empty, which ends the session (ResponseBuilder.Empty default)");
    }

    [Fact]
    public void Medium_LedgerMusicAudio_YieldsAudioAndNullRefusal()
    {
        var song = new Audio { Name = "Song", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(song);

        Assert.Equal("Audio", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue).ToString());
        Assert.Null(PlaybackLaunchBuilder.BuildVideoAppTransportRefusal(_builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue), "en-US"));
    }

    /// <summary>
    /// The token-ownership arm runs BEFORE the item resolve: when the AudioPlayer
    /// token names the ledger item, the answer is Audio whatever the kind (a movie
    /// can ride the audio-only transcode, where transport directives work).
    /// </summary>
    [Fact]
    public void Medium_TokenMatchingLedgerMovie_YieldsAudio()
    {
        var movie = new Movie { Name = "The Matrix", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(movie);
        Context context = TestHelpers.CreateContextWithToken(movie.Id.ToString());

        Assert.Equal("Audio", _builder.ResolvePlayingMedium(context, library.Object, queue).ToString());
    }

    /// <summary>
    /// A ledger id the library cannot resolve (deleted item) is Unknown, so a
    /// cold handler keeps its existing behavior.
    /// </summary>
    [Fact]
    public void Medium_UnresolvableLedgerItem_YieldsUnknown()
    {
        Guid deletedId = Guid.NewGuid();
        using DeviceQueueManager queue = TestHelpers.CreateDeviceQueueManager("medium-probe");
        queue.RecordLastPlayed("test-device", deletedId.ToString());
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(deletedId)).Returns((BaseItem?)null);

        Assert.Equal("Unknown", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue).ToString());
    }

    // ---- IsVideoAppMedium ----

    [Theory]
    [InlineData("Video", true)]
    [InlineData("LiveTv", true)]
    [InlineData("VideoAppAudiobook", true)]
    [InlineData("Audio", false)]
    [InlineData("Unknown", false)]
    public void IsVideoAppMedium_ClassifiesTheThreeVideoAppArms(string mediumName, bool expected)
    {
        Assert.Equal(expected, PlaybackLaunchBuilder.IsVideoAppMedium(Enum.Parse<PlaybackLaunchBuilder.PlayingMedium>(mediumName, ignoreCase: false)));
    }

    // ---- IsVideoAppLaunchItem: the ONE VideoApp kind predicate ----

    [Theory]
    [InlineData(typeof(Movie), true)]
    [InlineData(typeof(Episode), true)]
    [InlineData(typeof(MediaBrowser.Controller.LiveTv.LiveTvChannel), true)]
    [InlineData(typeof(Audio), false)]
    [InlineData(typeof(MediaBrowser.Controller.Entities.AudioBook), false)]
    [InlineData(typeof(Series), false)]
    public void IsVideoAppLaunchItem_MatchesTheMovieShapedKindList(Type itemType, bool expected)
    {
        var item = (BaseItem)Activator.CreateInstance(itemType)!;
        item.Id = Guid.NewGuid();

        Assert.Equal(expected, PlaybackLaunchBuilder.IsVideoAppLaunchItem(item));
    }

    [Fact]
    public void IsVideoAppLaunchItem_NullItem_False()
    {
        Assert.False(PlaybackLaunchBuilder.IsVideoAppLaunchItem(null));
    }

    // ---- IsActivelyPlaying: the PLAYING/BUFFER_UNDERRUN reading ----

    [Theory]
    [InlineData("PLAYING", true)]
    [InlineData("BUFFER_UNDERRUN", true)]
    [InlineData("PAUSED", false)]
    [InlineData("STOPPED", false)]
    [InlineData("FINISHED", false)]
    public void IsActivelyPlaying_CountsPlayingAndUnderrunOnly(string playerActivity, bool expected)
    {
        Assert.Equal(expected, PlaybackLaunchBuilder.IsActivelyPlaying(TestHelpers.CreateContextWithToken(null, playerActivity: playerActivity)));
    }

    [Fact]
    public void IsActivelyPlaying_NoAudioPlayerObject_False()
    {
        Assert.False(PlaybackLaunchBuilder.IsActivelyPlaying(TestHelpers.CreateTestContext()));
        Assert.False(PlaybackLaunchBuilder.IsActivelyPlaying(null));
    }
}

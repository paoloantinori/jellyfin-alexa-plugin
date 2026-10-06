using System;
using System.IO;
using global::Alexa.NET.Request;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
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
/// JF-626 added the ResolveCurrentPlayingItem section: the item-returning
/// sibling the Repeat/RateItem/playlist-edit resolvers consolidated onto.
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
    /// read + item resolve pair ResolvePlayingMedium performs). The route defaults
    /// to VideoApp (the recording site a Movie/Channel/Book ledger entry comes
    /// from); the JF-568 audio-route arms pass <see cref="DeviceQueueManager.LaunchRoute.Audio"/>.
    /// </summary>
    private static (Mock<ILibraryManager> Library, DeviceQueueManager Queue) LedgerWith(BaseItem item, string deviceId = "test-device", DeviceQueueManager.LaunchRoute route = DeviceQueueManager.LaunchRoute.VideoApp)
    {
        DeviceQueueManager queue = TestHelpers.CreateDeviceQueueManager("medium-probe");
        queue.RecordLastPlayed(deviceId, item.Id.ToString(), route);
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
        // The ordinary music shape: the AudioPlayer pipeline owns the stream (route
        // Audio), so transport directives genuinely work.
        var song = new Audio { Name = "Song", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(song, route: DeviceQueueManager.LaunchRoute.Audio);

        Assert.Equal("Audio", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue).ToString());
        Assert.Null(PlaybackLaunchBuilder.BuildVideoAppTransportRefusal(_builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue), "en-US"));
    }

    /// <summary>
    /// JF-625 seek mode: a song recorded on the VideoApp route (a single-song launch or
    /// the album concat's ledger entry) classifies VideoAppAudio, and the transport
    /// intents answer the honest refusal: an AudioPlayer.Play over the stream would be
    /// a second, unstoppable audio (no VideoApp.Stop exists).
    /// </summary>
    [Fact]
    public void Medium_LedgerMusicVideoApp_YieldsVideoAppAudioAndRefusal()
    {
        var song = new Audio { Name = "Song", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(song);

        var medium = _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue);
        Assert.Equal("VideoAppAudio", medium.ToString());
        Assert.NotNull(PlaybackLaunchBuilder.BuildVideoAppTransportRefusal(medium, "en-US"));
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
    /// JF-568 incident chain: a video-KIND item (Movie/Episode) launched on the
    /// AUDIO route (the JF-507 audio-only transcode, the JF-589 audio-route
    /// episodes) records the video-kind item in the ledger WITH route Audio; a
    /// subsequent PlaybackNearlyFinished radio enqueue (PostPlayBehavior=AutoPlay)
    /// moves the AudioPlayer token to the radio track WITHOUT recording. The
    /// recorded Audio route must keep the classification Audio (the audio pipeline
    /// owns the stream; transport directives work), not read token != ledger +
    /// video kind as VideoApp displacement.
    /// </summary>
    [Fact]
    public void Medium_LedgerMovieAudioRoute_TokenMovedToRadioTrack_YieldsAudio()
    {
        var movie = new Movie { Name = "The Matrix", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(movie, route: DeviceQueueManager.LaunchRoute.Audio);
        Context tokenMovedToRadioTrack = TestHelpers.CreateContextWithToken(Guid.NewGuid().ToString());

        Assert.Equal("Audio", _builder.ResolvePlayingMedium(tokenMovedToRadioTrack, library.Object, queue).ToString());
        Assert.Null(PlaybackLaunchBuilder.BuildVideoAppTransportRefusal(_builder.ResolvePlayingMedium(tokenMovedToRadioTrack, library.Object, queue), "en-US"));
    }

    /// <summary>
    /// JF-568 twin of the incident chain on the Episode kind (the JF-589 .strm
    /// audio-route shape): route Audio keeps the classification Audio even with no
    /// AudioPlayer token at all (fresh session after the audio-route launch).
    /// </summary>
    [Fact]
    public void Medium_LedgerEpisodeAudioRoute_WithoutToken_YieldsAudio()
    {
        var episode = new Episode { Name = "Pilot", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(episode, route: DeviceQueueManager.LaunchRoute.Audio);

        Assert.Equal("Audio", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue).ToString());
    }

    /// <summary>
    /// JF-568 displacement pin: the SAME video-kind ledger shape on the VideoApp
    /// route with the token moved still classifies Video (a VideoApp launch never
    /// updates the token, so the mismatch IS displacement).
    /// </summary>
    [Fact]
    public void Medium_LedgerMovieVideoAppRoute_TokenMoved_YieldsVideo()
    {
        var movie = new Movie { Name = "The Matrix", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(movie, route: DeviceQueueManager.LaunchRoute.VideoApp);
        Context tokenMoved = TestHelpers.CreateContextWithToken(Guid.NewGuid().ToString());

        Assert.Equal("Video", _builder.ResolvePlayingMedium(tokenMoved, library.Object, queue).ToString());
    }

    /// <summary>
    /// JF-568: a book the AUDIO route launched (NativeControlsForBooks off, the
    /// flat /Audio path) classifies Audio: the AudioPlayer transport directives
    /// work on that stream. Same semantics the token-ownership arm already gives a
    /// flat-audio book whose token matches.
    /// </summary>
    [Fact]
    public void Medium_LedgerAudioBookAudioRoute_TokenMoved_YieldsAudio()
    {
        var book = new MediaBrowser.Controller.Entities.AudioBook { Name = "Book", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(book, route: DeviceQueueManager.LaunchRoute.Audio);
        Context tokenMoved = TestHelpers.CreateContextWithToken(Guid.NewGuid().ToString());

        Assert.Equal("Audio", _builder.ResolvePlayingMedium(tokenMoved, library.Object, queue).ToString());
    }

    /// <summary>
    /// JF-568 no-regression pin: a queue file persisted by a pre-JF-568 plugin has
    /// NO route field, so the route reads null and the classification must keep
    /// today's kind-based behavior byte-for-byte (video kind + token moved =
    /// Video; music kind = Audio). The JSON is hand-written to the pre-JF-568
    /// persisted shape (no lastPlayedLaunchRoute member) and loaded through the
    /// ctor's disk path, exactly the upgrade scenario.
    /// </summary>
    [Fact]
    public void Medium_LegacyQueueFileWithoutRoute_KeepsKindBasedClassification()
    {
        var movie = new Movie { Name = "Old Movie", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Old Song", Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(movie.Id)).Returns(movie);
        library.Setup(l => l.GetItemById(song.Id)).Returns(song);

        string dir = TestHelpers.CreateRegisteredTempDir("medium-legacy");
        File.WriteAllText(Path.Combine(dir, "queue_legacy-video-device.json"), LegacyQueueJson(movie.Id));
        File.WriteAllText(Path.Combine(dir, "queue_legacy-music-device.json"), LegacyQueueJson(song.Id));
        using var queue = new DeviceQueueManager(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceQueueManager>.Instance);

        Context tokenMoved = TestHelpers.CreateContextWithToken(Guid.NewGuid().ToString(), "legacy-video-device");
        Context musicContext = TestHelpers.CreateTestContext("legacy-music-device");

        Assert.Equal("Video", _builder.ResolvePlayingMedium(tokenMoved, library.Object, queue).ToString());
        Assert.Equal("Audio", _builder.ResolvePlayingMedium(musicContext, library.Object, queue).ToString());
    }

    /// <summary>
    /// A <see cref="DeviceQueueManager"/> constructed over a pre-JF-568 persisted
    /// queue file naming the given item as the device's last play (the ONLY way a
    /// null recorded route exists; <c>RecordLastPlayed</c> always records one).
    /// The file must exist before the ctor loads it, so this builds its own
    /// one-file temp dir rather than riding <c>TestHelpers.CreateDeviceQueueManager</c>
    /// (the JF-540 cross-loading rationale); hoisted here on the third identical
    /// construction (the JF-713 convention).
    /// </summary>
    private static DeviceQueueManager LegacyQueueWith(Guid lastPlayedItemId, string deviceId)
    {
        string dir = TestHelpers.CreateRegisteredTempDir($"legacy-queue-{deviceId}");
        File.WriteAllText(Path.Combine(dir, $"queue_{deviceId}.json"), LegacyQueueJson(lastPlayedItemId));
        return new DeviceQueueManager(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceQueueManager>.Instance);
    }

    /// <summary>
    /// A pre-JF-568 persisted queue file: every then-existing member, no
    /// lastPlayedLaunchRoute (the field JF-568 added).
    /// </summary>
    private static string LegacyQueueJson(Guid lastPlayedItemId)
        => $"{{\"itemIds\":[],\"currentIndex\":-1,\"repeatMode\":\"None\",\"playbackOrder\":\"Default\","
            + $"\"lastModifiedUtc\":\"2026-09-01T00:00:00Z\",\"currentPositionTicks\":0,"
            + $"\"itemPositionState\":{{}},\"activeLaunchBaseMs\":{{}},\"pendingLaunchBaseMs\":{{}},"
            + $"\"lastPlayedItemId\":\"{lastPlayedItemId}\"}}";

    /// <summary>
    /// A ledger id the library cannot resolve (deleted item) is Unknown, so a
    /// cold handler keeps its existing behavior.
    /// </summary>
    [Fact]
    public void Medium_UnresolvableLedgerItem_YieldsUnknown()
    {
        Guid deletedId = Guid.NewGuid();
        using DeviceQueueManager queue = TestHelpers.CreateDeviceQueueManager("medium-probe");
        queue.RecordLastPlayed("test-device", deletedId.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(deletedId)).Returns((BaseItem?)null);

        Assert.Equal("Unknown", _builder.ResolvePlayingMedium(TestHelpers.CreateTestContext(), library.Object, queue).ToString());
    }

    // ---- ResolveCurrentPlayingItem: the ONE item resolver (JF-626) ----

    /// <summary>A session already holding the full now-playing item (the free-resolution shape).</summary>
    private static SessionInfo SessionHolding(BaseItem item)
    {
        SessionInfo session = TestHelpers.CreateTestSession(
            new Mock<ISessionManager>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        session.FullNowPlayingItem = item;
        return session;
    }

    /// <summary>
    /// JF-626 fix (b): a held <c>FullNowPlayingItem</c> returns without ANY library
    /// resolve (the pre-JF-626 playlist-edit shape re-resolved the session DTO's id).
    /// </summary>
    [Fact]
    public void CurrentItem_FullNowPlayingItemHeld_ReturnedWithoutLibraryResolve()
    {
        var song = new Audio { Name = "Held Song", Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();

        BaseItem? item = _builder.ResolveCurrentPlayingItem(
            TestHelpers.CreateTestContext(), SessionHolding(song), library.Object, queueManager: null);

        Assert.Same(song, item);
        library.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>
    /// JF-625 seek-mode arm of the shared resolver: a VideoApp-routed ledger entry
    /// that resolves to a plain Audio track displaced the stale AudioPlayer token
    /// (which names the pre-launch track); the ledger track is current.
    /// </summary>
    [Fact]
    public void CurrentItem_VideoAppRoutedAudioLedger_StaleToken_LedgerTrackWins()
    {
        var nowSong = new Audio { Name = "Seek Mode Song", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(nowSong);
        Context staleToken = TestHelpers.CreateContextWithToken(Guid.NewGuid().ToString());

        Assert.Same(nowSong, _builder.ResolveCurrentPlayingItem(staleToken, null, library.Object, queue));
    }

    /// <summary>
    /// JF-447 codec arm: a sleep-timer composite token still names its track, and
    /// the audio-routed ledger pin (the sleep launch is an inline directive that
    /// never records, so the ledger still names the older launch track) neither
    /// displaces it nor even resolves (the cheap guards run before the resolve).
    /// </summary>
    [Fact]
    public void CurrentItem_CompositeSleepToken_ResolvesOverLedgerPin()
    {
        var armedTrack = new Audio { Name = "Armed Track", Id = Guid.NewGuid() };
        var launchTrack = new Audio { Name = "Launch Track", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(launchTrack, route: DeviceQueueManager.LaunchRoute.Audio);
        library.Setup(l => l.GetItemById(armedTrack.Id)).Returns(armedTrack);
        Context compositeToken = TestHelpers.CreateContextWithToken($"{armedTrack.Id}|sleep:638800000000000000");

        Assert.Same(armedTrack, _builder.ResolveCurrentPlayingItem(compositeToken, null, library.Object, queue));
        library.Verify(l => l.GetItemById(launchTrack.Id), Times.Never);
    }

    /// <summary>
    /// The no-manager-anywhere shape (null queueManager AND a null Plugin.Instance,
    /// this class's baseline): the ledger arms stay off, so a token that misses the
    /// library falls to the session's held item. Post-JF-627 null alone no longer
    /// means this (see the fallback pin); only the absence of BOTH managers does.
    /// </summary>
    [Fact]
    public void CurrentItem_NoManagerAnywhere_TokenMissFallsToSessionItem()
    {
        var sessionSong = new Audio { Name = "Session Song", Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);
        Context unresolvableToken = TestHelpers.CreateContextWithToken(Guid.NewGuid().ToString());

        Assert.Same(
            sessionSong,
            _builder.ResolveCurrentPlayingItem(unresolvableToken, SessionHolding(sessionSong), library.Object, queueManager: null));
    }

    /// <summary>
    /// JF-626 review pin: the shared predicate widened RateItem's old
    /// route == VideoApp requirement to the classifier's null-route doctrine
    /// (a legacy pre-JF-568 file with no route member classifies by KIND, so a
    /// video-kind ledger entry displaces exactly as a VideoApp-routed one does).
    /// The legacy JSON is hand-written to the pre-JF-568 persisted shape and
    /// loaded through the ctor's disk path, mirroring Medium_LegacyQueueFileWithoutRoute.
    /// </summary>
    [Fact]
    public void CurrentItem_LegacyNullRouteVideoLedger_DisplacesLikeVideoAppRoute()
    {
        var movie = new Movie { Name = "Legacy Movie", Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(movie.Id)).Returns(movie);

        using var queue = LegacyQueueWith(movie.Id, "ci-legacy-device");

        Context staleToken = TestHelpers.CreateContextWithToken(Guid.NewGuid().ToString(), "ci-legacy-device");

        Assert.Same(movie, _builder.ResolveCurrentPlayingItem(staleToken, null, library.Object, queue));
    }

    /// <summary>
    /// JF-627: the null contract unified on the classifier-half idiom. A null
    /// queueManager no longer disables the ledger arms; it falls back to
    /// Plugin.Instance's manager (exactly <see cref="PlaybackLaunchBuilder.ResolvePlayingMedium"/>'s
    /// shape), so the playlist-edit family's calls resolve the displaced VideoApp
    /// item like every explicit-manager caller's do. RED on the pre-JF-627 tree
    /// (the stale token item won; the ledger was never read).
    /// </summary>
    [Fact]
    public void CurrentItem_NullQueueManager_FallsBackToPluginInstanceLedger()
    {
        var movie = new Movie { Name = "Fallback Movie", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(movie, "ci-fallback-device");
        Context staleToken = TestHelpers.CreateContextWithToken(Guid.NewGuid().ToString(), "ci-fallback-device");

        using var _ = TestHelpers.SwapPluginLedgerScope(
            _config,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            queue,
            "ci-fallback-plugin");

        Assert.Same(movie, _builder.ResolveCurrentPlayingItem(staleToken, null, library.Object, queueManager: null));
    }

    /// <summary>
    /// JF-785 Leg A at the resolver level: the guarded callers' flag refuses the
    /// non-displacement tail (no token, no session item), while the default
    /// keeps the deliberate unbounded stance (RateItem's JF-626 shape) and
    /// resolves the same entry.
    /// </summary>
    [Fact]
    public void CurrentItem_GuardedCaller_TailRefused_DefaultKeepsTail_JF785()
    {
        var oldSong = new Audio { Name = "Days Old Song", Id = Guid.NewGuid() };
        var (library, queue) = LedgerWith(oldSong, "ci-tail-jf785", route: DeviceQueueManager.LaunchRoute.Audio);

        // Default (RateItem/Repeat/SetPlaybackSpeed): the tail answers.
        Assert.Same(oldSong, _builder.ResolveCurrentPlayingItem(
            TestHelpers.CreateTestContext("ci-tail-jf785"), null, library.Object, queue));

        // The guarded caller (favorite/media/loop/playlist-edit): refused.
        Assert.Null(_builder.ResolveCurrentPlayingItem(
            TestHelpers.CreateTestContext("ci-tail-jf785"), null, library.Object, queue, allowLedgerTailAnswers: false));
    }

    /// <summary>
    /// JF-785 Leg A, the preserved half: the flag does NOT narrow the
    /// displacement arm (it requires a live mismatched token, so it is
    /// evidence-backed); a guarded caller during a VideoApp launch still
    /// resolves the displaced movie, identically to the default.
    /// </summary>
    [Fact]
    public void CurrentItem_GuardedCaller_DisplacementArmStillAnswers_JF785()
    {
        var movie = TestHelpers.CreateMovie("Displacing Movie");
        var (library, queue) = LedgerWith(movie, "ci-displace-jf785");
        Context staleToken = TestHelpers.CreateContextWithToken(Guid.NewGuid().ToString(), "ci-displace-jf785");

        Assert.Same(movie, _builder.ResolveCurrentPlayingItem(
            staleToken, null, library.Object, queue, allowLedgerTailAnswers: false));
    }

    /// <summary>
    /// The JF-627 lockstep guard (the JF-625 miss-class class): for EVERY ledger
    /// item-kind x route shape (the full 5 x 3 matrix: Movie, Episode,
    /// LiveTvChannel, AudioBook, Audio across VideoApp, Audio, and the legacy
    /// null route), the item resolver's displacement decision equals the
    /// classifier's non-Audio classification. A future launch kind or ladder
    /// arm added to ONE reader but not the other fails here even though each
    /// reader's own suite stays green. The legacy leg covers the pre-JF-568
    /// persisted null-route shape through the hand-written file (the only way a
    /// null route exists; <c>RecordLastPlayed</c> always records one).
    /// </summary>
    [Theory]
    [InlineData(typeof(Movie), "VideoApp")]
    [InlineData(typeof(Movie), "Audio")]
    [InlineData(typeof(Movie), "Legacy")]
    [InlineData(typeof(Episode), "VideoApp")]
    [InlineData(typeof(Episode), "Audio")]
    [InlineData(typeof(Episode), "Legacy")]
    [InlineData(typeof(MediaBrowser.Controller.LiveTv.LiveTvChannel), "VideoApp")]
    [InlineData(typeof(MediaBrowser.Controller.LiveTv.LiveTvChannel), "Audio")]
    [InlineData(typeof(MediaBrowser.Controller.LiveTv.LiveTvChannel), "Legacy")]
    [InlineData(typeof(MediaBrowser.Controller.Entities.AudioBook), "VideoApp")]
    [InlineData(typeof(MediaBrowser.Controller.Entities.AudioBook), "Audio")]
    [InlineData(typeof(MediaBrowser.Controller.Entities.AudioBook), "Legacy")]
    [InlineData(typeof(Audio), "VideoApp")]
    [InlineData(typeof(Audio), "Audio")]
    [InlineData(typeof(Audio), "Legacy")]
    public void Lockstep_DisplacementDecision_EqualsClassifierNonAudioClassification(Type ledgerKind, string routeName)
    {
        var ledgerItem = (BaseItem)Activator.CreateInstance(ledgerKind)!;
        ledgerItem.Id = Guid.NewGuid();
        ledgerItem.Name = $"Ledger {ledgerKind.Name}";
        var staleItem = new Audio { Name = "Stale Token Track", Id = Guid.NewGuid() };

        void AssertLockstep(ILibraryManager library, DeviceQueueManager queue, string deviceId)
        {
            Context staleToken = TestHelpers.CreateContextWithToken(staleItem.Id.ToString(), deviceId);
            PlaybackLaunchBuilder.PlayingMedium classified = _builder.ResolvePlayingMedium(staleToken, library, queue);
            BaseItem? resolved = _builder.ResolveCurrentPlayingItem(staleToken, null, library, queue);

            // The one invariant: the resolver hands back the ledger item exactly
            // when the classifier says a non-Audio medium owns the device.
            if (classified == PlaybackLaunchBuilder.PlayingMedium.Audio)
            {
                Assert.Same(staleItem, resolved);
            }
            else
            {
                Assert.Same(ledgerItem, resolved);
            }
        }

        if (routeName == "Legacy")
        {
            using var legacyQueue = LegacyQueueWith(ledgerItem.Id, $"lockstep-legacy-{ledgerKind.Name}");
            var library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetItemById(ledgerItem.Id)).Returns(ledgerItem);
            library.Setup(l => l.GetItemById(staleItem.Id)).Returns(staleItem);

            AssertLockstep(library.Object, legacyQueue, $"lockstep-legacy-{ledgerKind.Name}");
        }
        else
        {
            var route = Enum.Parse<DeviceQueueManager.LaunchRoute>(routeName);
            var (library, queue) = LedgerWith(ledgerItem, "lockstep-device", route);
            library.Setup(l => l.GetItemById(staleItem.Id)).Returns(staleItem);

            AssertLockstep(library.Object, queue, "lockstep-device");
        }
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

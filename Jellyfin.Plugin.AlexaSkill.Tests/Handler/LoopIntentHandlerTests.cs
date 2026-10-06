using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-450: de-DE, fr-FR, fr-CA and it-IT declare the custom loop vocabulary
/// (LoopAllOnIntent / LoopAllOffIntent / RepeatSingleOnIntent) instead of the
/// AMAZON.LoopOn/LoopOff built-ins used by the other 13 locales. These tests pin
/// that both names route to the same handlers and set the matching repeat mode.
/// JF-635 item 2 added the migration shapes mirroring RateItemIntentHandlerTests:
/// the token leg surviving a cleared session (PlaybackStopped), the JF-629 idle
/// guard, the JF-632 VideoApp displacement refusal, and the live one-shot
/// seek-mode refusal (2026-09-25 17:12, "saltare la canzone" -> RepeatSingleOn).
/// </summary>
[Collection("Plugin")]
public class LoopIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManager;

    public LoopIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManager = new Mock<ILibraryManager>();
    }

    private static IntentRequest IntentRequestFor(string intentName) =>
        new() { Intent = new Intent { Name = intentName }, Locale = "en-US", RequestId = "test-req" };

    private static Context ContextWithPlayingToken(string token, string deviceId = "test-device")
    {
        Context context = TestHelpers.CreateTestContext(deviceId);
        context.AudioPlayer = new PlaybackState { Token = token, OffsetInMilliseconds = 42_000 };
        return context;
    }

    private SessionInfo CreateSession()
    {
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.PlayState = new PlayerStateInfo();
        return session;
    }

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    // The ONE Audio factory (TestHelpers.CreateSong), reached through a
    // name-only alias so the three call shapes below stay one-liners.
    private static Audio Song(string name) => TestHelpers.CreateSong(name);

    /// <summary>The progress report the handler fired, if any (the suite's
    /// invocations-scan idiom, one copy for this file's three asserter sites).</summary>
    private PlaybackProgressInfo? AppliedProgressInfo()
        => _sessionManagerMock
            .Invocations
            .Select(i => i.Arguments.OfType<PlaybackProgressInfo>().FirstOrDefault())
            .FirstOrDefault(i => i != null);

    // The owning handler for each custom locale intent name (the built-in twin of the
    // same row is claimed by the same instance).
    private BaseHandler CreateOwner(string customIntentName, DeviceQueueManager? queueManager = null) => customIntentName switch
    {
        IntentNames.LoopAllOn => new LoopOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, queueManager),
        IntentNames.LoopAllOff => new LoopOffIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, queueManager),
        _ => new LoopSongOnIntentHandler(_sessionManagerMock.Object, _config, _libraryManager.Object, _loggerFactory, queueManager),
    };

    private void SetupLibraryResolves(params BaseItem[] items)
    {
        foreach (BaseItem item in items)
        {
            _libraryManager.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }
    }

    public static TheoryData<string, string> HandledIntentNames => new()
    {
        { "AMAZON.LoopOnIntent", IntentNames.LoopAllOn },
        { "AMAZON.LoopOffIntent", IntentNames.LoopAllOff },
        { IntentNames.LoopSongOn, IntentNames.RepeatSingleOn },
    };

    [Theory]
    [MemberData(nameof(HandledIntentNames))]
    public void CanHandle_AcceptsBothBuiltInAndCustomLocaleName(string builtInName, string customName)
    {
        BaseHandler handler = CreateOwner(customName);

        Assert.True(handler.CanHandle(IntentRequestFor(builtInName)), builtInName);
        Assert.True(handler.CanHandle(IntentRequestFor(customName)), customName);
        Assert.False(handler.CanHandle(IntentRequestFor("PlaySongIntent")));
    }

    public static TheoryData<string, RepeatMode> IntentToRepeatMode => new()
    {
        { IntentNames.LoopAllOn, RepeatMode.RepeatAll },
        { IntentNames.LoopAllOff, RepeatMode.RepeatNone },
        { IntentNames.RepeatSingleOn, RepeatMode.RepeatOne },
    };

    [Theory]
    [MemberData(nameof(IntentToRepeatMode))]
    public async Task HandleAsync_SetsMatchingRepeatMode(string intentName, RepeatMode expectedMode)
    {
        BaseHandler handler = CreateOwner(intentName);
        Audio song = Song("Happy Path");
        SetupLibraryResolves(song);
        Context context = ContextWithPlayingToken(song.Id.ToString());
        SessionInfo session = CreateSession();

        SkillResponse response = await handler.HandleAsync(IntentRequestFor(intentName), context, CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);

        PlaybackProgressInfo? info = AppliedProgressInfo();
        Assert.NotNull(info);
        Assert.Equal(expectedMode, info!.RepeatMode);
        Assert.Equal(song.Id, info.ItemId);
        Assert.Equal(TimeSpan.FromMilliseconds(context.AudioPlayer.OffsetInMilliseconds).Ticks, info.PositionTicks);

        // Live incident 2026-09-22 (battery test 5): the mode landed server-side but
        // the response was speech-less, which reads on-device as "nothing happened".
        string expectedConfirm = expectedMode switch
        {
            RepeatMode.RepeatAll => "RepeatAllEnabled",
            RepeatMode.RepeatOne => "RepeatSongEnabled",
            _ => "RepeatDisabled",
        };
        Assert.Contains(ResponseStrings.Get(expectedConfirm, "en-US"), TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(IntentNames.LoopAllOn)]
    [InlineData(IntentNames.LoopAllOff)]
    [InlineData(IntentNames.RepeatSingleOn)]
    public async Task HandleAsync_NoAudioContext_ReturnsNoMediaPlayingInsteadOfThrowing(string intentName)
    {
        // Review finding (JF-450): "attiva ripetizione" from an open session with
        // nothing playing must answer with the no-media tell, not a Guid crash.
        BaseHandler handler = CreateOwner(intentName);
        Context context = TestHelpers.CreateTestContext(); // No AudioPlayer state.

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(intentName), context, CreateUser(), CreateSession(), CancellationToken.None);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NoMediaPlaying", "en-US"), speech, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-629 no-regression pin: Jellyfin's PlaybackStopped clears the session's
    /// now-playing entries while the AudioPlayer token survives, so the loop toggle
    /// must still apply the mode to the TOKEN's item (the resolver's token leg; the
    /// pre-migration body read the token alone, the migration must not lose it).
    /// </summary>
    [Fact]
    public async Task HandleAsync_TokenSurvivesPlaybackStopped_StillAppliesMode()
    {
        BaseHandler handler = CreateOwner(IntentNames.RepeatSingleOn);
        Audio song = Song("Survivor");
        SetupLibraryResolves(song);
        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = null;
        session.NowPlayingItem = null; // the cleared-session shape

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(IntentNames.RepeatSingleOn), ContextWithPlayingToken(song.Id.ToString()),
            CreateUser(), session, CancellationToken.None);

        PlaybackProgressInfo? info = AppliedProgressInfo();
        Assert.NotNull(info);
        Assert.Equal(song.Id, info!.ItemId);
        Assert.Equal(RepeatMode.RepeatOne, info.RepeatMode);
        Assert.Contains(
            ResponseStrings.Get("RepeatSongEnabled", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The JF-629 idle guard: with neither an AudioPlayer token nor a session
    /// now-playing item, nothing is playing NOW, and the resolver's unbounded ledger
    /// tail (a days-old AUDIO-routed last-played) must not take the mode write.
    /// </summary>
    [Fact]
    public async Task HandleAsync_IdleDevice_OldAudioLedger_AnswersNoMediaWithoutWriting()
    {
        DeviceQueueManager queues = TestHelpers.CreateDeviceQueueManager("loop-idle");
        Audio oldSong = Song("Days Old");
        SetupLibraryResolves(oldSong);
        queues.RecordLastPlayed("loop-idle-device", oldSong.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        BaseHandler handler = CreateOwner(IntentNames.RepeatSingleOn, queues);
        SessionInfo session = CreateSession(); // no token in context, no session item

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(IntentNames.RepeatSingleOn), TestHelpers.CreateTestContext("loop-idle-device"),
            CreateUser(), session, CancellationToken.None);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal(RepeatMode.RepeatNone, session.PlayState!.RepeatMode);
    }

    /// <summary>
    /// The JF-632 medium gate, VideoApp displacement shape (the RateItem mirror):
    /// the stale music token names the last SONG while a VideoApp-routed movie
    /// plays. The video owns the screen, so the loop toggle must refuse honestly
    /// and write nothing, never apply the mode to the stale song.
    /// </summary>
    [Fact]
    public async Task HandleAsync_StaleTokenVideoDisplacement_RefusesWithoutWriting()
    {
        DeviceQueueManager queues = TestHelpers.CreateDeviceQueueManager("loop-displace");
        Audio oldSong = Song("Old Song");
        var movie = new Movie { Name = "Current Movie", Id = Guid.NewGuid(), Path = "/movies/c.mkv" };
        SetupLibraryResolves(oldSong, movie);
        queues.RecordLastPlayed("loop-displace-device", movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        BaseHandler handler = CreateOwner(IntentNames.LoopAllOn, queues);
        SessionInfo session = CreateSession();
        session.PlayState!.RepeatMode = RepeatMode.RepeatNone;

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(IntentNames.LoopAllOn), ContextWithPlayingToken(oldSong.Id.ToString(), "loop-displace-device"),
            CreateUser(), session, CancellationToken.None);

        Assert.Contains(
            ResponseStrings.Get("CannotRepeatContent", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal(RepeatMode.RepeatNone, session.PlayState!.RepeatMode);
    }

    /// <summary>
    /// The live 2026-09-25 17:12 shape (JF-635 item 2): a one-shot
    /// "saltare la canzone" over seek-mode (VideoApp-routed music) playback carries
    /// NO AudioPlayer token and an empty session; the recorded ledger route is the
    /// only evidence. The honest answer is the seek-mode loop refusal, not the
    /// no-media tell the DTO-only token read used to give.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SeekModeOneShot_RefusesHonestlyInsteadOfNoMedia()
    {
        DeviceQueueManager queues = TestHelpers.CreateDeviceQueueManager("loop-seek");
        Audio seekSong = Song("Seek Mode Song");
        SetupLibraryResolves(seekSong);
        queues.RecordLastPlayed("loop-seek-device", seekSong.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        BaseHandler handler = CreateOwner(IntentNames.RepeatSingleOn, queues);
        SessionInfo session = CreateSession(); // no token, no session item: the one-shot shape

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(IntentNames.RepeatSingleOn), TestHelpers.CreateTestContext("loop-seek-device"),
            CreateUser(), session, CancellationToken.None);

        Assert.Contains(
            ResponseStrings.Get("CannotRepeatInSeekMode", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal(RepeatMode.RepeatNone, session.PlayState!.RepeatMode);
    }

    /// <summary>
    /// The guard's session-evidence arm: no token, but the session's now-playing
    /// DTO names the track (the continuing-session shape). Current evidence exists,
    /// the ledger is audio-routed, so the mode applies to the session's item.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NoTokenSessionDtoEvidence_AppliesModeToSessionItem()
    {
        DeviceQueueManager queues = TestHelpers.CreateDeviceQueueManager("loop-session");
        Audio song = Song("Session Song");
        SetupLibraryResolves(song);
        queues.RecordLastPlayed("loop-session-device", song.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        BaseHandler handler = CreateOwner(IntentNames.LoopAllOff, queues);
        SessionInfo session = CreateSession();
        session.NowPlayingItem = new BaseItemDto { Id = song.Id, Name = song.Name };

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(IntentNames.LoopAllOff), TestHelpers.CreateTestContext("loop-session-device"),
            CreateUser(), session, CancellationToken.None);

        PlaybackProgressInfo? info = AppliedProgressInfo();
        Assert.NotNull(info);
        Assert.Equal(song.Id, info!.ItemId);
        Assert.Equal(RepeatMode.RepeatNone, info.RepeatMode);
        Assert.Contains(
            ResponseStrings.Get("RepeatDisabled", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-785 red proof (the migration leg): the full-item-without-DTO shape
    /// (held FullNowPlayingItem, no DTO, no token; the evidence legs are
    /// documented on HasCurrentPlaybackEvidence; the ledger is Audio-routed to
    /// the SAME track, the production shape of the pre-PlaybackStarted window)
    /// resolves the held item and the mode applies to it. RED on the pre-JF-785
    /// tree: the DTO-only guard answered the no-media tell here.
    /// </summary>
    [Fact]
    public async Task HandleAsync_FullItemHeldWithoutDto_AppliesModeToHeldItem_JF785()
    {
        DeviceQueueManager queues = TestHelpers.CreateDeviceQueueManager("loop-held-jf785");
        Audio song = Song("Held Song");
        SetupLibraryResolves(song);
        queues.RecordLastPlayed("loop-held-jf785-device", song.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        BaseHandler handler = CreateOwner(IntentNames.RepeatSingleOn, queues);
        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = song;
        Assert.Null(session.NowPlayingItem); // the full-item-WITHOUT-DTO shape

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(IntentNames.RepeatSingleOn), TestHelpers.CreateTestContext("loop-held-jf785-device"),
            CreateUser(), session, CancellationToken.None);

        PlaybackProgressInfo? info = AppliedProgressInfo();
        Assert.NotNull(info);
        Assert.Equal(song.Id, info!.ItemId);
        Assert.Equal(RepeatMode.RepeatOne, info.RepeatMode);
        Assert.Contains(
            ResponseStrings.Get("RepeatSongEnabled", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-785 Leg A (the unresolvable-evidence door; the resolver doc owns the
    /// contract): a now-playing DTO whose id does not resolve (Guid.Empty
    /// stands for the deleted-mid-play shape) plus a days-old AUDIO-routed
    /// ledger entry; the mode write must not land on the ledger item, the
    /// no-media tell answers. RED on the pre-JF-785 tree: the tail flowed
    /// through and the mode landed on the days-old item.
    /// </summary>
    [Fact]
    public async Task HandleAsync_UnresolvableDtoStaleLedger_NoModeWrite_JF785()
    {
        DeviceQueueManager queues = TestHelpers.CreateDeviceQueueManager("loop-door-jf785");
        Audio oldSong = Song("Days Old Song");
        SetupLibraryResolves(oldSong);
        queues.RecordLastPlayed("loop-door-jf785-device", oldSong.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        BaseHandler handler = CreateOwner(IntentNames.LoopAllOn, queues);
        SessionInfo session = CreateSession();
        session.NowPlayingItem = new BaseItemDto { Id = Guid.Empty, Name = "Ghost Track" };

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(IntentNames.LoopAllOn), TestHelpers.CreateTestContext("loop-door-jf785-device"),
            CreateUser(), session, CancellationToken.None);

        Assert.Contains(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal(RepeatMode.RepeatNone, session.PlayState!.RepeatMode);
    }

    /// <summary>
    /// The belt's SAME-ITEM shape (the arm only ResolveScreenOwningMedium closes,
    /// pinned here because no classifier-only arm fires): the native-controls
    /// delegation re-records the SAME track on the VideoApp route while the
    /// AudioPlayer token keeps naming it, so the classifier's token-ownership arm
    /// answers Audio and would let the mode write through. The belt must override
    /// it to the seek-mode refusal.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SameItemSeekModeShape_BeltOverridesTokenOwnership()
    {
        DeviceQueueManager queues = TestHelpers.CreateDeviceQueueManager("loop-sameitem");
        Audio seekSong = Song("Same Item Seek Song");
        SetupLibraryResolves(seekSong);
        queues.RecordLastPlayed("loop-sameitem-device", seekSong.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        BaseHandler handler = CreateOwner(IntentNames.RepeatSingleOn, queues);
        SessionInfo session = CreateSession(); // empty session: the token is the only request evidence

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(IntentNames.RepeatSingleOn), ContextWithPlayingToken(seekSong.Id.ToString(), "loop-sameitem-device"),
            CreateUser(), session, CancellationToken.None);

        Assert.Contains(
            ResponseStrings.Get("CannotRepeatInSeekMode", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal(RepeatMode.RepeatNone, session.PlayState!.RepeatMode);
    }

    /// <summary>
    /// The string-decision pin: a VideoApp-routed AUDIOBOOK must take the
    /// video-family refusal (CannotRepeatContent names audiobooks honestly), not
    /// the seek-mode music line, even though AudioBook subclasses Audio and would
    /// otherwise pass a naive "ledger item is Audio" music-shaped check.
    /// </summary>
    [Fact]
    public async Task HandleAsync_VideoAppRoutedAudiobook_VideoFamilyRefusalNotMusicLine()
    {
        DeviceQueueManager queues = TestHelpers.CreateDeviceQueueManager("loop-book");
        var book = new AudioBook { Name = "VideoApp Book", Id = Guid.NewGuid(), Path = "/books/b.m4b" };
        SetupLibraryResolves(book);
        queues.RecordLastPlayed("loop-book-device", book.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        BaseHandler handler = CreateOwner(IntentNames.RepeatSingleOn, queues);

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(IntentNames.RepeatSingleOn), TestHelpers.CreateTestContext("loop-book-device"),
            CreateUser(), CreateSession(), CancellationToken.None);

        Assert.Contains(
            ResponseStrings.Get("CannotRepeatContent", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            ResponseStrings.Get("CannotRepeatInSeekMode", "en-US"),
            TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        _sessionManagerMock.Verify(
            s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()), Times.Never);
    }
}

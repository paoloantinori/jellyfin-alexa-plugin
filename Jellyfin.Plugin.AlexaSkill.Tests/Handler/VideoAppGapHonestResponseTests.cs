using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-564 pins: the honest per-medium responses for the VideoApp GAP cells
/// (Next/Previous during video and live TV, Pause/Cancel during video, live TV
/// and VideoApp audiobooks) plus the two invariants that must NOT move:
/// a VideoApp-family medium never receives an AudioPlayer.Play directive (the
/// stale-queue guard), and the music semantics stay byte-identical (a cold
/// ledger or an audio-owned item keeps the pre-JF-564 behavior).
/// JF-847/JF-868 pins: the pause branch speaks a stopped-by-voice Tell for a
/// VideoApp-family medium on EVERY context playerActivity. The JF-847
/// PLAYING-only split was superseded by the 2026-10-11 device evidence (corr
/// ab55dc39, de7f892a): the common VideoApp shape is a bare STOPPED context
/// during active playback, the AudioPlayer.Stop directive DOES stop the video,
/// so the former cannot-pause line contradicted the behavior. JF-870 refines
/// the WORDING only: the audiobook branch speaks the book line, every other
/// VideoApp medium keeps the video line.
/// </summary>
[Collection("Plugin")]
public class VideoAppGapHonestResponseTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");

    public VideoAppGapHonestResponseTests()
    {
        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "jf564-gap-tests");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    // === fixtures ===

    private NextIntentHandler CreateNextHandler(DeviceQueueManager queueManager)
        => new(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager);

    private PreviousIntentHandler CreatePreviousHandler(DeviceQueueManager queueManager)
        => new(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager);

    private PauseIntentHandler CreatePauseHandler(DeviceQueueManager queueManager)
        => new(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory, _fx.LibraryManager.Object, queueManager);

    private static IntentRequest CreateIntent(string name, string locale = "en-US")
        => new()
        {
            Type = "IntentRequest",
            Locale = locale,
            Intent = new global::Alexa.NET.Request.Intent { Name = name }
        };

    private SessionInfo CreateSession(params BaseItem[] queueItems)
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.NowPlayingQueue = queueItems.Select(i => new QueueItem { Id = i.Id }).ToList();
        session.FullNowPlayingItem = queueItems.FirstOrDefault();
        return session;
    }

    /// <summary>
    /// A ledger that records <paramref name="lastPlayed"/> as the device's last play,
    /// with the library mock resolving every given item by id.
    /// </summary>
    private DeviceQueueManager CreateLedger(string deviceId, BaseItem lastPlayed, params BaseItem[] resolve)
        => CreateLedger(deviceId, DeviceQueueManager.LaunchRoute.VideoApp, lastPlayed, resolve);

    private DeviceQueueManager CreateLedger(string deviceId, DeviceQueueManager.LaunchRoute route, BaseItem lastPlayed, params BaseItem[] resolve)
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("jf564-" + deviceId);
        queueManager.RecordLastPlayed(deviceId, lastPlayed.Id.ToString(), route);
        _fx.LibraryManager.Setup(x => x.GetItemById(lastPlayed.Id)).Returns(lastPlayed);
        foreach (BaseItem item in resolve)
        {
            _fx.LibraryManager.Setup(x => x.GetItemById(item.Id)).Returns(item);
        }

        return queueManager;
    }

    private static Audio Song(string name) => new() { Name = name, Id = Guid.NewGuid(), Path = $"/music/{name}.mp3" };

    private static MediaBrowser.Controller.Entities.Movies.Movie Movie(string name)
        => new() { Name = name, Id = Guid.NewGuid(), Path = $"/movies/{name}.mkv" };

    private static MediaBrowser.Controller.LiveTv.LiveTvChannel Channel(string name)
        => new() { Name = name, Id = Guid.NewGuid() };

    private static AudioBook Chapter(string name)
        => new() { Name = name, Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), Path = $"/audiobooks/{name}.mp3" };

    // === Next/Previous during VIDEO: honest line + the stale-queue guard ===

    [Fact]
    public async Task Next_DuringVideo_StaleMusicQueue_SpeaksHonestLineAndNeverPlaysAudio()
    {
        // THE audit cell: the movie's VideoApp launch left the session's music queue
        // untouched (FullNowPlayingItem still the pre-video song), so the old code
        // emitted an AudioPlayer.Play of the next SONG mid-video. The ledger says a
        // movie is playing: the honest line answers and no audio directive leaves.
        var song1 = Song("Stale Track One");
        var song2 = Song("Stale Track Two");
        var movie = Movie("Current Movie");

        string deviceId = "jf564-next-video";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie, song1, song2);
        var handler = CreateNextHandler(ledger);

        // The stale shape: token still names song1 (VideoApp never updates it) while
        // the session queue/now-playing also still describe the pre-video music state.
        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            TestHelpers.CreateContextWithToken(song1.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(song1, song2),
            CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains("can't move between videos", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        TestHelpers.AssertNoAudioPlayDirective(response);
    }

    [Fact]
    public async Task Previous_DuringVideo_SpeaksHonestLine()
    {
        var song1 = Song("Stale Track One");
        var movie = Movie("Current Movie");

        string deviceId = "jf564-prev-video";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie, song1);
        var handler = CreatePreviousHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PreviousIntent"),
            TestHelpers.CreateContextWithToken(song1.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(song1),
            CancellationToken.None);

        Assert.Contains("can't move between videos", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        TestHelpers.AssertNoAudioPlayDirective(response);
    }

    [Fact]
    public async Task Next_DuringVideo_ItalianLocale_UsesItalianString()
    {
        var movie = Movie("Film");

        string deviceId = "jf564-next-video-it";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie);
        var handler = CreateNextHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent", "it-IT"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Contains("non posso cambiare video", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    // === Next/Previous during LIVE TV: honest line ===

    [Fact]
    public async Task Next_DuringLiveTv_SpeaksHonestChannelLine()
    {
        // The channel launch pins the session queue to the single channel, so the old
        // code hit the "already at last item" silent Empty. The ledger says live TV.
        var channel = Channel("CNN");

        string deviceId = "jf564-next-livetv";
        DeviceQueueManager ledger = CreateLedger(deviceId, channel);
        var handler = CreateNextHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(channel),
            CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains("change the channel", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        TestHelpers.AssertNoAudioPlayDirective(response);
    }

    [Fact]
    public async Task Previous_DuringLiveTv_SpeaksHonestChannelLine()
    {
        var channel = Channel("CNN");

        string deviceId = "jf564-prev-livetv";
        DeviceQueueManager ledger = CreateLedger(deviceId, channel);
        var handler = CreatePreviousHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PreviousIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(channel),
            CancellationToken.None);

        Assert.Contains("change the channel", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        TestHelpers.AssertNoAudioPlayDirective(response);
    }

    // === Next/Previous during a VideoApp AUDIOBOOK: silent Empty, still no audio directive ===

    [Fact]
    public async Task Next_DuringVideoAppAudiobook_ReturnsSilentEmptyWithoutAudioDirective()
    {
        // The book cell is NOT one of the honest-line cells (chapter navigation is its
        // own feature), but the guard still applies: a stale music queue must not
        // produce an AudioPlayer.Play mid-book.
        var song1 = Song("Stale Track One");
        var song2 = Song("Stale Track Two");
        var chapter = Chapter("Book Chapter 3");

        string deviceId = "jf564-next-book";
        DeviceQueueManager ledger = CreateLedger(deviceId, chapter, song1, song2);
        var handler = CreateNextHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            TestHelpers.CreateContextWithToken(song1.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(song1, song2),
            CancellationToken.None);

        // The pre-JF-564 shape for this cell (silent Empty), now without the audio
        // directive the stale queue could have produced.
        Assert.Null(response.Response.OutputSpeech);
        TestHelpers.AssertNoAudioPlayDirective(response);
    }

    // === Pause/Cancel during VideoApp family: stopped line, session end, Stop kept ===

    // === JF-847/JF-868: the line is ALWAYS a stopped-by-voice Tell for a VideoApp
    // === medium, on every context playerActivity (2026-10-11 device evidence);
    // === JF-870: the audiobook branch speaks its own book line.

    [Theory]
    [InlineData("AMAZON.PauseIntent", null)]
    [InlineData("AMAZON.CancelIntent", null)]
    [InlineData("AMAZON.PauseIntent", "PLAYING")]
    [InlineData("AMAZON.PauseIntent", "STOPPED")]
    [InlineData("AMAZON.PauseIntent", "BUFFER_UNDERRUN")]
    [InlineData("AMAZON.PauseIntent", "IDLE")]
    [InlineData("AMAZON.CancelIntent", "STOPPED")]
    public async Task PauseAndCancel_DuringVideo_AnyPlayerActivity_SpeakStoppedLine(string intentName, string? activity)
    {
        // Supersedes the JF-847 state-aware split: the 2026-10-11 device sessions
        // (corr ab55dc39, de7f892a) showed the common VideoApp shape is a bare
        // STOPPED context during active playback, yet the AudioPlayer.Stop
        // directive DOES stop the video, so the former cannot-pause line
        // contradicted the behavior. Null activity rides the same branch via the
        // plain test context. Cancel shares the branch, so it shares the line.
        var movie = Movie("Current Movie");

        string deviceId = "jf847-pause-video-" + (activity ?? "no-activity");
        DeviceQueueManager ledger = CreateLedger(deviceId, movie);
        var handler = CreatePauseHandler(ledger);

        // The VideoApp launch never writes the AudioPlayer token (the JF-563
        // doctrine), so any activity with no token is a live shape.
        var context = activity is null
            ? TestHelpers.CreateTestContext(deviceId)
            : TestHelpers.CreateContextWithToken(null, deviceId, activity);
        var response = await handler.HandleAsync(
            CreateIntent(intentName),
            context,
            TestHelpers.CreateTestUser(),
            CreateSession(movie),
            CancellationToken.None);

        // The video-stopped line, session ended as a Tell (an IntentRequest, so the
        // JF-299 event rules do not apply), and the AudioPlayer.Stop directive kept
        // for any displaced audio stream.
        TestHelpers.AssertVideoStoppedTell(response);
    }

    [Fact]
    public async Task Pause_DuringVideo_StaleTokenStoppedContext_SpeaksStoppedLine()
    {
        // Memory pin for the shape the JF-847 branch was built around (the
        // 2026-10-09 stale-token STOPPED context, then treated as an inert stop):
        // JF-868 speaks the stopped line here TOO, per the maintainer's directive
        // that the VideoApp family always speaks a stopped line (the book branch
        // its own wording since JF-870). If a future
        // device session shows the directive genuinely inert against THIS shape,
        // the evidence supersedes and this pin changes with it; until then the
        // speak-truth-vs-behavior contradiction (voice said cannot-pause while the
        // video stopped) stays closed on every VideoApp shape.
        var stale = Movie("Previously Stopped Movie");
        var movie = Movie("Relaunched Movie");

        string deviceId = "jf868-pause-video-stale-token";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie, stale);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateContextWithToken(stale.Id.ToString(), deviceId, "STOPPED"),
            TestHelpers.CreateTestUser(),
            CreateSession(movie),
            CancellationToken.None);

        TestHelpers.AssertVideoStoppedTell(response);
    }

    [Fact]
    public async Task Pause_DuringLiveTv_SpeaksStoppedLine()
    {
        var channel = Channel("CNN");

        string deviceId = "jf564-pause-livetv";
        DeviceQueueManager ledger = CreateLedger(deviceId, channel);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(channel),
            CancellationToken.None);

        TestHelpers.AssertVideoStoppedTell(response);
    }

    [Theory]
    [InlineData("AMAZON.PauseIntent")]
    [InlineData("AMAZON.CancelIntent")]
    public async Task PauseAndCancel_DuringVideoAppAudiobook_SpeaksBookStoppedLine(string intentName)
    {
        // JF-870: the stopped line names the medium, so the audiobook branch
        // speaks BookStoppedByVoice ("Ho fermato il libro.", the 2026-10-12
        // device round's wrong-medium "fermato il video" fix), never the video
        // line the JF-868 era pinned here. Cancel shares the branch, so it
        // shares the book line.
        var chapter = Chapter("Book Chapter 3");

        string deviceId = "jf870-pause-book-" + intentName;
        DeviceQueueManager ledger = CreateLedger(deviceId, chapter);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent(intentName),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(chapter),
            CancellationToken.None);

        TestHelpers.AssertBookStoppedTell(response);
    }

    [Fact]
    public async Task Pause_DuringVideoAppAudiobook_OnAudioPlayerPath_KeepsMusicSemantics()
    {
        // A book with NativeControlsForBooks OFF rides the flat AudioPlayer path: the
        // token owns it, so pause is a REAL pause (the medium classifier must not
        // mistake it for the VideoApp book cell).
        var chapter = Chapter("Flat Audio Book");

        string deviceId = "jf564-pause-flat-book";
        DeviceQueueManager ledger = CreateLedger(deviceId, chapter);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateContextWithToken(chapter.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(chapter),
            CancellationToken.None);

        // The JF-482 pause shape (flag default ON): open session, minimal pause word,
        // reprompt, and the Stop directive.
        Assert.False(response.Response.ShouldEndSession);
        Assert.Contains("Paused", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        TestHelpers.AssertHasAudioPlayerStopDirective(response);
    }

    // === Stop during video: the docs-mandated silent shape is unchanged ===

    [Fact]
    public async Task Stop_DuringVideo_KeepsSilentSessionEndingShape()
    {
        var movie = Movie("Current Movie");

        string deviceId = "jf564-stop-video";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.StopIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(movie),
            CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
        TestHelpers.AssertHasAudioPlayerStopDirective(response);
    }

    // === The music path must not regress ===

    [Fact]
    public async Task Pause_DuringAudio_KeepsJf482OpenSessionBehavior()
    {
        var song = Song("Current Song");

        string deviceId = "jf564-pause-audio";
        DeviceQueueManager ledger = CreateLedger(deviceId, DeviceQueueManager.LaunchRoute.Audio, song);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(song),
            CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        Assert.Equal("Paused.", TestHelpers.GetSpeechText(response));
        // JF-872: the reprompt is gone (maintainer call 2026-10-13); the open
        // session alone opens the listen window.
        Assert.Null(response.Response.Reprompt);
        TestHelpers.AssertHasAudioPlayerStopDirective(response);
    }

    [Fact]
    public async Task Next_DuringAudio_QueueAdvance_StillPlaysNextTrack()
    {
        // Queue advance moves only the token while the ledger pins the user-initiated
        // play: that mismatch is the ordinary music shape, and Next keeps playing the
        // queue.
        var track1 = Song("Album Track One");
        var track2 = Song("Album Track Two");

        string deviceId = "jf564-next-audio";
        DeviceQueueManager ledger = CreateLedger(deviceId, DeviceQueueManager.LaunchRoute.Audio, track1, track2);
        var handler = CreateNextHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            TestHelpers.CreateContextWithToken(track2.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(track1, track2),
            CancellationToken.None);

        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        // Round-3: the advance replaces the actively-playing stream the context names
        // (this shape's token IS the next track), so the directive may carry the
        // JF-655 launch generation; the pin is that it plays track2.
        Assert.True(StreamTokenCodec.NamesItem(directive.AudioItem.Stream.Token, track2.Id.ToString()));
    }

    [Fact]
    public async Task Next_ColdLedger_KeepsEmptyQueueBehavior()
    {
        // Nothing recorded for this device: the handler must answer exactly as before
        // JF-564 (a cold handler never changes its music semantics).
        var handler = CreateNextHandler(TestHelpers.CreateDeviceQueueManager("jf564-next-cold"));

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            TestHelpers.CreateTestContext("jf564-next-cold"),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        Assert.Empty(response.Response.Directives ?? new List<IDirective>());
    }

    [Fact]
    public async Task Pause_ColdLedger_KeepsPreJf564PauseShape()
    {
        var handler = CreatePauseHandler(TestHelpers.CreateDeviceQueueManager("jf564-pause-cold"));

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateTestContext("jf564-pause-cold"),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        // Identical to the legacy pause response (flag ON by default since JF-488).
        Assert.Equal(
            Newtonsoft.Json.JsonConvert.SerializeObject(BaseHandler.BuildPauseResponse(true, "en-US")),
            Newtonsoft.Json.JsonConvert.SerializeObject(response));
    }

    // === JF-858: the flat transport gates' corroboration bound ===
    //
    // The classifier's ledger-only route/kind ladder stayed deliberately unbounded
    // through JF-789 (the window doc's carve-out), so an AGED RESOLVABLE VideoApp
    // entry kept answering the flat gates' refusals on idle devices. JF-858 bounds
    // the GATES (not the classifier): a VideoApp-family medium survives only when
    // corroborated by live evidence (the ONE HasCurrentPlaybackEvidence predicate)
    // OR a within-window stamp; the one quadrant the bound opens is AGED and
    // evidence-free, where each family falls to its ordinary no-ledger path.

    /// <summary>
    /// JF-858 red proof, the pause family's flip: an aged resolvable VideoApp-routed
    /// movie with NO live evidence (no AudioPlayer token, no session now-playing) is
    /// the idle-device shape, and the ordinary pause line must answer exactly as on a
    /// cold device (the AudioPlayer.Stop directive is identical on both paths; only
    /// the spoken line differed). RED on the pre-JF-858 tree: the VideoApp
    /// stopped-by-voice line answered over the idle device. (JF-868 review: the
    /// flip itself is unaffected by the always-stopped-line change: the aged arm
    /// degrades to Unknown and the AUDIO pause path, which never spoke the VideoApp
    /// line.)
    /// </summary>
    [Fact]
    public async Task Pause_DaysOldResolvableVideoAppLedger_NoEvidence_AnswersOrdinaryPauseShape_JF858()
    {
        var movie = Movie("Days Old Movie");

        string deviceId = "jf858-pause-aged";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie);
        TestHelpers.BackdateLastPlayedStamp(ledger, deviceId, TimeSpan.FromDays(3));
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        // The byte-identical cold-device comparison (the Pause_ColdLedger pin's
        // shape): the corroborated gate degraded the classification to Unknown and
        // the ordinary JF-482 pause response answered.
        Assert.Equal(
            Newtonsoft.Json.JsonConvert.SerializeObject(BaseHandler.BuildPauseResponse(true, "en-US")),
            Newtonsoft.Json.JsonConvert.SerializeObject(response));
    }

    /// <summary>
    /// JF-858 boundary pin, the EVIDENCE arm: an aged stamp does NOT degrade the
    /// classification when the session still holds the launched item, because the
    /// held item is the observable live-evidence leg (the minute-90-of-a-movie
    /// shape; a plain VideoApp movie stream has no server-side stamp refresh, so
    /// a window-only bound would lose the video-stopped line exactly there).
    /// JF-868: the line is VideoStoppedByVoice on every VideoApp shape, the
    /// audiobook branch its own line since JF-870.
    /// </summary>
    [Fact]
    public async Task Pause_DaysOldResolvableVideoAppLedger_SessionHoldsMovie_SpeaksStoppedLine_JF858()
    {
        var movie = Movie("Long Movie Minute Ninety");

        string deviceId = "jf858-pause-aged-live";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie);
        TestHelpers.BackdateLastPlayedStamp(ledger, deviceId, TimeSpan.FromDays(3));
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(movie),
            CancellationToken.None);

        TestHelpers.AssertVideoStoppedTell(response);
    }

    /// <summary>
    /// JF-858 boundary pin, the WINDOW arm: a FRESH VideoApp entry keeps the
    /// video-stopped line even with no live evidence (the just-launched video whose
    /// Jellyfin session was dropped), matching the pre-JF-858 answer for every
    /// within-window shape.
    /// </summary>
    [Fact]
    public async Task Pause_FreshVideoAppLedger_NoEvidence_SpeaksStoppedLine_JF858()
    {
        var movie = Movie("Fresh Movie");

        string deviceId = "jf858-pause-fresh";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        TestHelpers.AssertVideoStoppedTell(response);
    }

    /// <summary>
    /// JF-858 red proof, the Next/Previous family's flip: the same aged
    /// evidence-free shape must answer the queue logic's silent Empty, not the
    /// VideoApp navigation refusal (the queue logic structurally cannot act: its
    /// current-item resolution reads exactly the evidence legs the corroboration
    /// found absent). RED on the pre-JF-858 tree: CannotNavigateVideoByVoice
    /// answered over the idle device.
    /// </summary>
    [Fact]
    public async Task Next_DaysOldResolvableVideoAppLedger_NoEvidence_SilentEmpty_JF858()
    {
        var movie = Movie("Days Old Movie");

        string deviceId = "jf858-next-aged";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie);
        TestHelpers.BackdateLastPlayedStamp(ledger, deviceId, TimeSpan.FromDays(3));
        var handler = CreateNextHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        Assert.Empty(response.Response.Directives ?? new List<IDirective>());
    }

    /// <summary>
    /// JF-858 boundary pin, the twin's flip: Previous shares
    /// ServeAdjacentQueueItem's one gate, so the aged evidence-free shape flips
    /// identically (the silent Empty).
    /// </summary>
    [Fact]
    public async Task Previous_DaysOldResolvableVideoAppLedger_NoEvidence_SilentEmpty_JF858()
    {
        var movie = Movie("Days Old Movie");

        string deviceId = "jf858-prev-aged";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie);
        TestHelpers.BackdateLastPlayedStamp(ledger, deviceId, TimeSpan.FromDays(3));
        var handler = CreatePreviousHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PreviousIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        Assert.Empty(response.Response.Directives ?? new List<IDirective>());
    }

    /// <summary>
    /// JF-858 boundary pin, the EVIDENCE arm at the navigation gate: the aged
    /// JF-564 audit shape (the stale music token still naming a queue member while
    /// the aged VideoApp movie entry sits in the ledger) KEEPS the refusal, because
    /// the token is live evidence; a window-only bound would have let the queue
    /// logic emit the parallel AudioPlayer.Play mid-video (the JF-564 double-audio
    /// class) at minute 61 of a movie.
    /// </summary>
    [Fact]
    public async Task Next_DaysOldVideoAppLedger_StaleTokenEvidence_StillRefuses_JF858()
    {
        var song1 = Song("Stale Track One");
        var song2 = Song("Stale Track Two");
        var movie = Movie("Long Movie Minute Ninety");

        string deviceId = "jf858-next-aged-live";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie, song1, song2);
        TestHelpers.BackdateLastPlayedStamp(ledger, deviceId, TimeSpan.FromDays(3));
        var handler = CreateNextHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            TestHelpers.CreateContextWithToken(song1.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(song1, song2),
            CancellationToken.None);

        Assert.Contains("can't move between videos", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        TestHelpers.AssertNoAudioPlayDirective(response);
    }
}

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
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("jf564-" + deviceId);
        queueManager.RecordLastPlayed(deviceId, lastPlayed.Id.ToString());
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

    // === Pause/Cancel during VideoApp family: honest line, session end, Stop kept ===

    [Theory]
    [InlineData("AMAZON.PauseIntent")]
    [InlineData("AMAZON.CancelIntent")]
    public async Task PauseAndCancel_DuringVideo_SpeakHonestLine(string intentName)
    {
        var movie = Movie("Current Movie");

        string deviceId = "jf564-pause-video";
        DeviceQueueManager ledger = CreateLedger(deviceId, movie);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent(intentName),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(movie),
            CancellationToken.None);

        // Honest line, session ended as a Tell (an IntentRequest, so the JF-299
        // event rules do not apply), and the AudioPlayer.Stop directive kept for any
        // displaced audio stream.
        Assert.Contains("can't pause video by voice", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
        TestHelpers.AssertHasAudioPlayerStopDirective(response);
        TestHelpers.AssertNoAudioPlayDirective(response);
    }

    [Fact]
    public async Task Pause_DuringLiveTv_SpeaksHonestLine()
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

        Assert.Contains("can't pause video by voice", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
        TestHelpers.AssertHasAudioPlayerStopDirective(response);
    }

    [Fact]
    public async Task Pause_DuringVideoAppAudiobook_SpeaksHonestLine()
    {
        var chapter = Chapter("Book Chapter 3");

        string deviceId = "jf564-pause-book";
        DeviceQueueManager ledger = CreateLedger(deviceId, chapter);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(chapter),
            CancellationToken.None);

        Assert.Contains("can't pause video by voice", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
        TestHelpers.AssertHasAudioPlayerStopDirective(response);
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
        DeviceQueueManager ledger = CreateLedger(deviceId, song);
        var handler = CreatePauseHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.PauseIntent"),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(song),
            CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        Assert.Equal("Paused.", TestHelpers.GetSpeechText(response));
        Assert.NotNull(response.Response.Reprompt);
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
        DeviceQueueManager ledger = CreateLedger(deviceId, track1, track2);
        var handler = CreateNextHandler(ledger);

        var response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            TestHelpers.CreateContextWithToken(track2.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(track1, track2),
            CancellationToken.None);

        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(track2.Id.ToString(), directive.AudioItem.Stream.Token);
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
}

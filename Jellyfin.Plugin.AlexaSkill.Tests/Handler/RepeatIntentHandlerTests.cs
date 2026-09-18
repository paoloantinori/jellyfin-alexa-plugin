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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for RepeatIntentHandler (JF-562): music restarts the current track from
/// position 0; video/TV/audiobook answer the honest cannot-repeat tell; no current
/// item answers NoMediaPlaying; a stale AudioPlayer token defers to the device's
/// last-played record.
/// </summary>
[Collection("Plugin")]
public class RepeatIntentHandlerTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");

    public RepeatIntentHandlerTests()
    {
        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "repeat-tests");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private RepeatIntentHandler CreateHandler(DeviceQueueManager? queueManager = null)
    {
        return new RepeatIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.LoggerFactory,
            queueManager);
    }

    private static IntentRequest CreateRepeatRequest(string locale = "en-US")
    {
        return new IntentRequest
        {
            Type = "IntentRequest",
            Locale = locale,
            Intent = new global::Alexa.NET.Request.Intent { Name = "AMAZON.RepeatIntent" }
        };
    }

    private static Context CreateContextWithToken(string token, string deviceId = "test-device")
        => TestHelpers.CreateContextWithToken(token, deviceId);

    private SessionInfo CreateSessionWithNowPlaying(BaseItem item)
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.FullNowPlayingItem = item;
        return session;
    }

    /// <summary>
    /// The honest cannot-repeat answers carry NO AudioPlayer.Play directive (the
    /// failure shape is a spoken Tell; restarting audio would be the wrong answer);
    /// shared shape promoted to TestHelpers in JF-564.
    /// </summary>
    private static void AssertNoAudioPlayDirective(SkillResponse response)
        => TestHelpers.AssertNoAudioPlayDirective(response);

    [Fact]
    public void CanHandle_RepeatIntent_ReturnsTrue()
    {
        var handler = CreateHandler();

        Assert.True(handler.CanHandle(CreateRepeatRequest()));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Type = "IntentRequest",
            Locale = "en-US",
            Intent = new global::Alexa.NET.Request.Intent { Name = "AMAZON.StartOverIntent" }
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_MusicPlaying_RestartsCurrentTrackFromZero()
    {
        var handler = CreateHandler();
        var request = CreateRepeatRequest();
        var context = TestHelpers.CreateTestContext();
        var user = TestHelpers.CreateTestUser();

        var song = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Path = "/music/test.mp3"
        };

        var response = await handler.HandleAsync(request, context, user, CreateSessionWithNowPlaying(song), CancellationToken.None);

        Assert.NotNull(response);
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(PlayBehavior.ReplaceAll, directive.PlayBehavior);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(song.Id.ToString(), directive.AudioItem.Stream.Token);

        // Play responses end the session (the JF-299 rule) and are silent unless
        // AnnounceAudioPlays is on (opt-in default off, JF-352.4).
        Assert.True(response.Response.ShouldEndSession);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_MusicPlaying_AnnounceAudioPlaysOn_Speaks()
    {
        _fx.Config.AnnounceAudioPlays = true;
        var handler = CreateHandler();
        var request = CreateRepeatRequest();
        var context = TestHelpers.CreateTestContext();
        var user = TestHelpers.CreateTestUser();

        var song = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Path = "/music/test.mp3"
        };

        var response = await handler.HandleAsync(request, context, user, CreateSessionWithNowPlaying(song), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Test Song", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_TokenOnly_NoSession_ResolvesItemAndRestarts()
    {
        var handler = CreateHandler();
        var request = CreateRepeatRequest();
        var user = TestHelpers.CreateTestUser();

        var song = new Audio
        {
            Name = "Token Song",
            Id = Guid.NewGuid(),
            Path = "/music/token.mp3"
        };

        _fx.LibraryManager.Setup(x => x.GetItemById(song.Id)).Returns(song);

        var response = await handler.HandleAsync(request, CreateContextWithToken(song.Id.ToString()), user, null!, CancellationToken.None);

        Assert.NotNull(response);
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(song.Id.ToString(), directive.AudioItem.Stream.Token);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task HandleAsync_MoviePlaying_HonestTellNoDirective()
    {
        var handler = CreateHandler();
        var request = CreateRepeatRequest();
        var context = TestHelpers.CreateTestContext();
        var user = TestHelpers.CreateTestUser();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Test Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/test.mkv"
        };

        var response = await handler.HandleAsync(request, context, user, CreateSessionWithNowPlaying(movie), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Empty(response.Response.Directives ?? new List<IDirective>());
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("can't repeat", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_EpisodePlaying_HonestTell()
    {
        var handler = CreateHandler();
        var request = CreateRepeatRequest();
        var context = TestHelpers.CreateTestContext();
        var user = TestHelpers.CreateTestUser();

        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Test Episode",
            Id = Guid.NewGuid(),
            Path = "/tv/test.mkv"
        };

        var response = await handler.HandleAsync(request, context, user, CreateSessionWithNowPlaying(episode), CancellationToken.None);

        Assert.NotNull(response);
        AssertNoAudioPlayDirective(response);
        Assert.Contains("can't repeat", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_AudioBookPlaying_HonestTellNotAudioRestart()
    {
        // AudioBook subclasses Audio, so this pins the exclusion ORDER: a book is
        // not a repeatable music track even though `is Audio` matches it.
        var handler = CreateHandler();
        var request = CreateRepeatRequest();
        var context = TestHelpers.CreateTestContext();
        var user = TestHelpers.CreateTestUser();

        var book = new AudioBook
        {
            Name = "Test Book",
            Id = Guid.NewGuid(),
            Path = "/books/test.m4b"
        };

        var response = await handler.HandleAsync(request, context, user, CreateSessionWithNowPlaying(book), CancellationToken.None);

        Assert.NotNull(response);
        AssertNoAudioPlayDirective(response);
        Assert.Contains("can't repeat", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_StaleToken_UsesDeviceLastPlayed_VideoAnswersHonestly()
    {
        // A VideoApp launch never updates context.AudioPlayer.Token, so the token
        // names the previously played SONG while the movie plays. The device's
        // last-played record wins: the recorded VIDEO item is what is current.
        var queueManager = TestHelpers.CreateDeviceQueueManager("repeat-stale");
        var handler = CreateHandler(queueManager);
        var request = CreateRepeatRequest();
        var user = TestHelpers.CreateTestUser();

        var song = new Audio
        {
            Name = "Old Song",
            Id = Guid.NewGuid(),
            Path = "/music/old.mp3"
        };
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "New Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/new.mkv"
        };

        string deviceId = "repeat-stale-device";
        queueManager.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        _fx.LibraryManager.Setup(x => x.GetItemById(movie.Id)).Returns(movie);
        _fx.LibraryManager.Setup(x => x.GetItemById(song.Id)).Returns(song);

        var context = CreateContextWithToken(song.Id.ToString(), deviceId);
        var response = await handler.HandleAsync(request, context, user, null!, CancellationToken.None);

        Assert.NotNull(response);
        AssertNoAudioPlayDirective(response);
        Assert.Contains("can't repeat", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-568: the SAME token-vs-ledger mismatch shape, but the video-kind ledger
    /// entry was recorded with the AUDIO route (the JF-507 audio-only transcode
    /// launch; the token then moved on to the AutoPlay radio track without
    /// recording). That is NOT displacement: the audio pipeline owns the stream,
    /// so the token's item (the radio track) is what repeats.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AudioRouteVideoKindLedger_TokenMoved_DoesNotDisplace()
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("repeat-audio-route");
        var handler = CreateHandler(queueManager);
        var request = CreateRepeatRequest();
        var user = TestHelpers.CreateTestUser();

        var radioTrack = new Audio
        {
            Name = "Radio Track",
            Id = Guid.NewGuid(),
            Path = "/music/radio.mp3"
        };
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Audio-Route Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/audio-route.mkv"
        };

        string deviceId = "repeat-audio-route-device";
        queueManager.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        _fx.LibraryManager.Setup(x => x.GetItemById(movie.Id)).Returns(movie);
        _fx.LibraryManager.Setup(x => x.GetItemById(radioTrack.Id)).Returns(radioTrack);

        var context = CreateContextWithToken(radioTrack.Id.ToString(), deviceId);
        var response = await handler.HandleAsync(request, context, user, null!, CancellationToken.None);

        Assert.NotNull(response);
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(PlayBehavior.ReplaceAll, directive.PlayBehavior);
        Assert.Equal(radioTrack.Id.ToString(), directive.AudioItem.Stream.Token);
    }

    [Fact]
    public async Task HandleAsync_QueueAdvanced_TokenWinsOverOlderUserInitiatedPlay()
    {
        // The queue-advance shape (code-review finding on the first cut):
        // RecordLastPlayed pins the USER-INITIATED play (track 1) while the
        // Enqueue directives that advance the queue move only the AudioPlayer
        // token, so from track 2 onward token != lastPlayed. That mismatch is
        // NOT staleness: the newer token (track 2) is what is playing.
        var queueManager = TestHelpers.CreateDeviceQueueManager("repeat-advance");
        var handler = CreateHandler(queueManager);
        var request = CreateRepeatRequest();
        var user = TestHelpers.CreateTestUser();

        var track1 = new Audio
        {
            Name = "Album Track One",
            Id = Guid.NewGuid(),
            Path = "/music/t1.mp3"
        };
        var track2 = new Audio
        {
            Name = "Album Track Two",
            Id = Guid.NewGuid(),
            Path = "/music/t2.mp3"
        };

        string deviceId = "repeat-advance-device";
        queueManager.RecordLastPlayed(deviceId, track1.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        _fx.LibraryManager.Setup(x => x.GetItemById(track1.Id)).Returns(track1);
        _fx.LibraryManager.Setup(x => x.GetItemById(track2.Id)).Returns(track2);

        var context = CreateContextWithToken(track2.Id.ToString(), deviceId);
        var response = await handler.HandleAsync(request, context, user, null!, CancellationToken.None);

        Assert.NotNull(response);
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(PlayBehavior.ReplaceAll, directive.PlayBehavior);
        Assert.Equal(track2.Id.ToString(), directive.AudioItem.Stream.Token);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task HandleAsync_NoToken_DeviceLastPlayedVideo_HonestTell()
    {
        // No AudioPlayer token at all (fresh session whose only play was the
        // VideoApp movie): the device's last-played record resolves the movie.
        var queueManager = TestHelpers.CreateDeviceQueueManager("repeat-notoken");
        var handler = CreateHandler(queueManager);
        var request = CreateRepeatRequest();
        var user = TestHelpers.CreateTestUser();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Only Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/only.mkv"
        };

        string deviceId = "repeat-notoken-device";
        queueManager.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        _fx.LibraryManager.Setup(x => x.GetItemById(movie.Id)).Returns(movie);

        var context = TestHelpers.CreateTestContext(deviceId);
        var response = await handler.HandleAsync(request, context, user, null!, CancellationToken.None);

        Assert.NotNull(response);
        AssertNoAudioPlayDirective(response);
        Assert.Contains("can't repeat", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NothingPlaying_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateRepeatRequest();
        var context = TestHelpers.CreateTestContext();
        var user = TestHelpers.CreateTestUser();

        var response = await handler.HandleAsync(request, context, user, null!, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("nothing is currently playing", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_TokenItemNotFoundInLibrary_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateRepeatRequest();
        var user = TestHelpers.CreateTestUser();

        Guid deleted = Guid.NewGuid();
        _fx.LibraryManager.Setup(x => x.GetItemById(deleted)).Returns((BaseItem?)null);

        var response = await handler.HandleAsync(request, CreateContextWithToken(deleted.ToString()), user, null!, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("nothing is currently playing", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ItalianLocale_HonestTellUsesItalianString()
    {
        var handler = CreateHandler();
        var request = CreateRepeatRequest("it-IT");
        var context = TestHelpers.CreateTestContext();
        var user = TestHelpers.CreateTestUser();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Film",
            Id = Guid.NewGuid(),
            Path = "/movies/film.mkv"
        };

        var response = await handler.HandleAsync(request, context, user, CreateSessionWithNowPlaying(movie), CancellationToken.None);

        Assert.Contains("non posso ripetere", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Helper to deserialize AudioPlayerRequest from JSON (readonly properties
/// can't be set via object initializer).
/// </summary>
internal static class AlexaRequestFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static AudioPlayerRequest CreateAudioPlayerRequest(string type, string token, long offsetMs)
    {
        string json = $$"""
        {
            "type": "{{type}}",
            "requestId": "test-req-{{Guid.NewGuid()}}",
            "timestamp": "2024-01-01T00:00:00Z",
            "locale": "en-US",
            "token": "{{token}}",
            "offsetInMilliseconds": {{offsetMs}}
        }
        """;
        return JsonSerializer.Deserialize<AudioPlayerRequest>(json, JsonOptions)!;
    }

    internal static Context CreateContextWithAudioPlayer(string userId, string deviceId, string token, long offsetMs, string playerActivity)
    {
        string json = $$"""
        {
            "System": {
                "user": { "userId": "{{userId}}" },
                "device": { "deviceId": "{{deviceId}}" }
            },
            "AudioPlayer": {
                "token": "{{token}}",
                "offsetInMilliseconds": {{offsetMs}},
                "playerActivity": "{{playerActivity}}"
            }
        }
        """;
        return JsonSerializer.Deserialize<Context>(json, JsonOptions)!;
    }
}

/// <summary>
/// Tests for PlaybackNearlyFinished PostPlay integration.
/// </summary>
[Collection("Plugin")]
public class PlaybackNearlyFinishedPostPlayTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PlaybackNearlyFinishedEventHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();
    private const string DeviceId = "test-device";

    public PlaybackNearlyFinishedPostPlayTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "pnf-postplay-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public async Task QueueExhausted_StopMode_ReturnsEmpty()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.Stop;
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = TestHelpers.CreateSong("Test Song", itemId);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));
    }

    [Fact]
    public async Task QueueExhausted_AutoPlayMode_EnqueuesSimilarTracks()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var itemId = Guid.NewGuid();
        var radioTrackId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = TestHelpers.CreateSong("Test Song", itemId, new[] { "Rock" });

        var currentAudio = TestHelpers.CreateSong("Test Song", itemId, new[] { "Rock" });
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(currentAudio);

        var jellyfinUser = TestHelpers.CreateJellyfinUser(authProviderId: "test", passwordProviderId: "test", id: _userId);
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        var radioTrack = TestHelpers.CreateSong("Test Song", radioTrackId);
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());
        _libraryManagerMock.Setup(lm => lm.GetItemById(radioTrackId)).Returns(radioTrack);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(RadioModeState.IsEnabled(_userId, DeviceId));

        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(PlayBehavior.Enqueue, playDirective.PlayBehavior);
    }

    [Fact]
    public async Task QueueExhausted_AutoPlayMode_NoTracksFound_ReturnsEmpty()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = TestHelpers.CreateSong("Test Song", itemId, new[] { "Rock" });

        var currentAudio = TestHelpers.CreateSong("Test Song", itemId, new[] { "Rock" });
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(currentAudio);

        var jellyfinUser = TestHelpers.CreateJellyfinUser(authProviderId: "test", passwordProviderId: "test", id: _userId);
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>().AsReadOnly());

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));
    }

    // =====================================================================
    // JF-670: books never radio
    // =====================================================================

    // A finishing audiobook chapter must not seed PostPlay music radio even with
    // PostPlay=AutoPlay: the book ends in silence. The radio query mock returns a
    // track, so a regressed gate would enqueue it and fail every assertion here.
    [Fact]
    public async Task BookChapterExhausted_AutoPlayMode_DoesNotSeedRadio()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var chapter = TestHelpers.CreateSong("Chapter 1", chapterId, new[] { "Mystery" });
        chapter.ParentId = bookId;

        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(chapterId.ToString());
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = chapterId } };
        session.FullNowPlayingItem = chapter;

        _libraryManagerMock.Setup(lm => lm.GetItemById(chapterId)).Returns(chapter);
        _libraryManagerMock.Setup(lm => lm.GetItemById(bookId)).Returns(new AudioBook { Id = bookId, Name = "The Book" });

        var radioTrack = TestHelpers.CreateSong("Radio Song");
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));
        Assert.Equal(chapterId, Assert.Single(session.NowPlayingQueue).Id);
        _libraryManagerMock.Verify(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    // The gate keys on the BOOK shape (an Audio whose parent is an AudioBook), not on
    // Audio-ness: a song under a music album still seeds radio at the same decision
    // point. Without the ParentId discrimination this test and the one above could
    // not both pass.
    [Fact]
    public async Task SongExhausted_WithMusicAlbumParent_StillSeedsRadio()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var albumId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var song = TestHelpers.CreateSong("Test Song", songId, new[] { "Rock" });
        song.ParentId = albumId;

        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(songId.ToString());
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = songId } };
        session.FullNowPlayingItem = song;

        _libraryManagerMock.Setup(lm => lm.GetItemById(songId)).Returns(song);
        _libraryManagerMock.Setup(lm => lm.GetItemById(albumId)).Returns(new MusicAlbum { Id = albumId, Name = "The Album" });

        var radioTrackId = Guid.NewGuid();
        var radioTrack = TestHelpers.CreateSong("Radio Song", radioTrackId);
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());
        _libraryManagerMock.Setup(lm => lm.GetItemById(radioTrackId)).Returns(radioTrack);

        var jellyfinUser = TestHelpers.CreateJellyfinUser(authProviderId: "test", passwordProviderId: "test", id: _userId);
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(RadioModeState.IsEnabled(_userId, DeviceId));
        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(PlayBehavior.Enqueue, playDirective.PlayBehavior);
    }

    // A leftover radio flag from earlier music must not hand a book's last chapter to
    // the radio continuation either: the chapter never RECEIVES radio tracks (the
    // gate sits ahead of the radio-mode branch, not only on the PostPlay arm).
    [Fact]
    public async Task BookChapterExhausted_LeftoverRadioModeEnabled_DoesNotReceiveRadioTracks()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        RadioModeState.Enable(_userId, DeviceId);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var chapter = TestHelpers.CreateSong("Chapter 1", chapterId);
        chapter.ParentId = bookId;

        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(chapterId.ToString());
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = chapterId } };
        session.FullNowPlayingItem = chapter;

        _libraryManagerMock.Setup(lm => lm.GetItemById(chapterId)).Returns(chapter);
        _libraryManagerMock.Setup(lm => lm.GetItemById(bookId)).Returns(new AudioBook { Id = bookId, Name = "The Book" });

        var radioTrack = TestHelpers.CreateSong("Radio Song");
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        Assert.Equal(chapterId, Assert.Single(session.NowPlayingQueue).Id);
        _libraryManagerMock.Verify(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);

        RadioModeState.Disable(_userId, DeviceId);
    }

    // AudioBook DERIVES FROM Audio (reflection-probed 10.11.8 + 12.0.0), so the
    // radio paths' `as Audio` cast succeeds on a single-file book: without the gate
    // the AudioBook item itself seeds genre radio exactly like a song. The gate must
    // catch the AudioBook item directly, not only chapter shapes.
    [Fact]
    public async Task SingleFileBookExhausted_AutoPlayMode_DoesNotSeedRadio()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var bookId = Guid.NewGuid();
        var book = new AudioBook { Id = bookId, Name = "Single File Book" };
        book.Genres = new[] { "Mystery" };

        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(bookId.ToString());
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = bookId } };
        session.FullNowPlayingItem = book;

        _libraryManagerMock.Setup(lm => lm.GetItemById(bookId)).Returns(book);

        var radioTrack = TestHelpers.CreateSong("Radio Song");
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));
        Assert.Equal(bookId, Assert.Single(session.NowPlayingQueue).Id);
        _libraryManagerMock.Verify(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    // The initial chapters query is Recursive, so a chapter's direct parent can be a
    // plain subfolder (book/Disc 2/chapter): the gate walks the ancestor chain, it
    // does not stop at the first non-AudioBook hop.
    [Fact]
    public async Task SubfolderChapterExhausted_AutoPlayMode_DoesNotSeedRadio()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var bookId = Guid.NewGuid();
        var discFolderId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var chapter = TestHelpers.CreateSong("Chapter 11", chapterId);
        chapter.ParentId = discFolderId;

        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(chapterId.ToString());
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = chapterId } };
        session.FullNowPlayingItem = chapter;

        _libraryManagerMock.Setup(lm => lm.GetItemById(chapterId)).Returns(chapter);
        _libraryManagerMock.Setup(lm => lm.GetItemById(discFolderId))
            .Returns(new Folder { Id = discFolderId, Name = "Disc 2" });
        _libraryManagerMock.Setup(lm => lm.GetItemById(bookId))
            .Returns(new AudioBook { Id = bookId, Name = "The Book" });

        var radioTrack = TestHelpers.CreateSong("Radio Song");
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));
        Assert.Equal(chapterId, Assert.Single(session.NowPlayingQueue).Id);
        _libraryManagerMock.Verify(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task HasNextItem_DoesNotTriggerPostPlay()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(currentId.ToString());

        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = currentId },
            new() { Id = nextId }
        };
        session.FullNowPlayingItem = TestHelpers.CreateSong("Test Song", currentId);
        _libraryManagerMock.Setup(lm => lm.GetItemById(nextId)).Returns(TestHelpers.CreateSong("Test Song", nextId));

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should enqueue the next item, not trigger PostPlay
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));
        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task RadioModeOn_DoesNotTriggerPostPlay()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = TestHelpers.CreateSong("Test Song", itemId);
        RadioModeState.Enable(_userId, DeviceId);

        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns((JellyfinUser?)null);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Radio mode handles its own continuation, not PostPlay
        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
    }

    // =====================================================================
    // JF-324 episode auto-advance (AudioPlayer-routed TV content)
    // =====================================================================

    [Fact]
    public async Task EpisodeFinishing_AutoPlayMode_EnqueuesNextEpisode()
    {
        var seriesId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        var (currentEpisode, request, context, user, session) = SetupFinishingEpisode(currentId, seriesId, "S03E05");

        var nextEpisode = CreateEpisodeItem(nextId, seriesId, "S03E06");
        _libraryManagerMock.Setup(lm => lm.GetItemById(nextId)).Returns(nextEpisode);

        // Real direct-query shape: the finishing episode is STILL unplayed (its stop
        // is not reported yet), so it is the query's first answer; the branch must
        // skip it and enqueue the true next.
        SetupUnplayedEpisodes(currentEpisode, nextEpisode);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(PlayBehavior.Enqueue, playDirective.PlayBehavior);
        Assert.Equal(nextId.ToString(), playDirective.AudioItem.Stream.Token);
        Assert.Equal(currentId.ToString(), playDirective.AudioItem.Stream.ExpectedPreviousToken);
        Assert.Contains("stream", playDirective.AudioItem.Stream.Url, StringComparison.OrdinalIgnoreCase);

        // Gapless: no announcement, and the next episode joined the session queue so
        // the following advance resolves from it.
        Assert.Null(response.Response.OutputSpeech);
        Assert.Equal(2, session.NowPlayingQueue.Count);
        Assert.Equal(nextId, session.NowPlayingQueue[1].Id);

        // JF-299 event-response shape: AudioPlayer.Play only, session-ending.
        Assert.True(response.Response.ShouldEndSession);
        Assert.All(response.Response.Directives!, d => Assert.IsType<AudioPlayerPlayDirective>(d));
    }

    [Fact]
    public async Task EpisodeFinishing_AlreadyQueuedEpisodesSkipped()
    {
        // JF-409 self-reenqueue guard: the query may return episodes that are already
        // in the queue (their stops were never reported to Jellyfin); the branch must
        // pick the first candidate that is neither queued nor the finishing item.
        var seriesId = Guid.NewGuid();
        var earlierId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        var (currentEpisode, request, context, user, session) = SetupFinishingEpisode(currentId, seriesId, "S03E05");

        var earlierEpisode = CreateEpisodeItem(earlierId, seriesId, "S03E04");
        var nextEpisode = CreateEpisodeItem(nextId, seriesId, "S03E06");
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = earlierId },
            new() { Id = currentId }
        };

        _libraryManagerMock.Setup(lm => lm.GetItemById(nextId)).Returns(nextEpisode);

        SetupUnplayedEpisodes(earlierEpisode, currentEpisode, nextEpisode);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(nextId.ToString(), playDirective.AudioItem.Stream.Token);
    }

    [Fact]
    public async Task EpisodeFinishing_NoNextEpisode_PreservesEndOfQueue()
    {
        var seriesId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var (_, request, context, user, session) = SetupFinishingEpisode(currentId, seriesId, "Series finale");

        // True end of series: the unplayed-episodes query answers NOTHING for this
        // user. (A finale whose stop has not been reported yet answers [finale];
        // the skip-set resolves that shape to the same no-op.)
        SetupUnplayedEpisodes();

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(currentId, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public async Task MovieFinishing_AutoPlayMode_NoEpisodeAdvance()
    {
        // A movie has no natural next: the branch must not run for non-Episode items.
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var movieId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(movieId.ToString());

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = movieId, Name = "Test Movie" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = movieId } };
        session.FullNowPlayingItem = movie;
        _libraryManagerMock.Setup(lm => lm.GetItemById(movieId)).Returns(movie);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        VerifyNoEpisodeQuery();
    }

    [Fact]
    public async Task EpisodeFinishing_StopMode_NoEpisodeAdvance()
    {
        var seriesId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var (_, request, context, user, session) = SetupFinishingEpisode(currentId, seriesId, "S03E05");
        _config.DefaultPostPlayBehavior = PostPlayBehavior.Stop;

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        VerifyNoEpisodeQuery();
    }

    [Fact]
    public async Task EpisodeFinishing_VideosDisabled_SkipsAdvance()
    {
        var seriesId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var (_, request, context, user, session) = SetupFinishingEpisode(currentId, seriesId, "S03E05");

        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory, c => c.VideosEnabled = false, "pnf-postplay-test");
        try
        {
            SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
            VerifyNoEpisodeQuery();
            _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        }
        finally
        {
            TestHelpers.EnsurePluginInstance(
                _config, _loggerFactory, c => c.VideosEnabled = true, "pnf-postplay-test");
        }
    }

    [Fact]
    public async Task EpisodeFinishing_SeriesOutsideAllowedLibraries_SkipsAdvance()
    {
        // Per-user library authorization: the series must survive the same
        // library-filtered series query the intent path resolves names through.
        var seriesId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var (_, request, context, user, session) = SetupFinishingEpisode(currentId, seriesId, "S03E05");

        // Authorization query finds nothing: the series lives outside the user's
        // allowed libraries.
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>().AsReadOnly());

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        VerifyNoEpisodeQuery();
        Assert.Single(session.NowPlayingQueue);
    }

    [Fact]
    public async Task QueueExhausted_AutoPlayMode_SleepToken_EnqueuesSimilarTracks()
    {
        // JF-447 regression: the raw event token may carry a sleep suffix; the music
        // PostPopulate path must parse it via the shared codec instead of Guid.Parse,
        // which threw on the composite form at queue exhaustion.
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var itemId = Guid.NewGuid();
        var radioTrackId = Guid.NewGuid();
        string sleepToken = StreamTokenCodec.MintSleepTimerToken(itemId, DateTimeOffset.UtcNow.AddHours(1).UtcTicks);
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(sleepToken);

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = TestHelpers.CreateSong("Test Song", itemId, new[] { "Rock" });
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(TestHelpers.CreateSong("Test Song", itemId, new[] { "Rock" }));
        _userManagerMock.Setup(um => um.GetUserById(_userId))
            .Returns(TestHelpers.CreateJellyfinUser(authProviderId: "test", passwordProviderId: "test", id: _userId));

        var radioTrack = TestHelpers.CreateSong("Test Song", radioTrackId);
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());
        _libraryManagerMock.Setup(lm => lm.GetItemById(radioTrackId)).Returns(radioTrack);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(PlayBehavior.Enqueue, playDirective.PlayBehavior);
        Assert.Equal(radioTrackId.ToString(), playDirective.AudioItem.Stream.Token);
    }

    private static global::MediaBrowser.Controller.Entities.TV.Episode CreateEpisodeItem(Guid id, Guid seriesId, string name)
    {
        return new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = id,
            Name = name,
            SeriesId = seriesId,
            SeriesName = "Test Show"
        };
    }

    /// <summary>
    /// Common arrange for the episode auto-advance tests: AutoPlay config, a
    /// nearly-finished context for the episode, a single-item now-playing queue with
    /// the episode as the now-playing item, and the GetItemById/GetUserById/
    /// series-authorization setups. A test overrides on top of this whatever differs
    /// (queue contents, unplayed-episode answers, authorization result, PostPlay mode).
    /// </summary>
    private (global::MediaBrowser.Controller.Entities.TV.Episode episode, Request request, Context context, Entities.User user, SessionInfo session)
        SetupFinishingEpisode(Guid currentId, Guid seriesId, string name)
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(currentId.ToString());

        var currentEpisode = CreateEpisodeItem(currentId, seriesId, name);
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId } };
        session.FullNowPlayingItem = currentEpisode;

        _libraryManagerMock.Setup(lm => lm.GetItemById(currentId)).Returns(currentEpisode);
        _userManagerMock.Setup(um => um.GetUserById(_userId))
            .Returns(TestHelpers.CreateJellyfinUser(authProviderId: "test", passwordProviderId: "test", id: _userId));

        // Series authorization query returns the series (user may access it).
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.Series))))
            .Returns(new List<BaseItem> { new global::MediaBrowser.Controller.Entities.TV.Series { Id = seriesId, Name = "Test Show" } }.AsReadOnly());

        return (currentEpisode, request, context, user, session);
    }

    /// <summary>
    /// Answers the episode auto-advance's direct unplayed-episodes query (the real
    /// C1 contract: GetItemList with the series-scoped Episode filter, NOT NextUp,
    /// which returns at most one item when scoped by SeriesId) with the given
    /// candidates in order. No arguments means the query answers empty (end of
    /// series).
    /// </summary>
    private void SetupUnplayedEpisodes(params BaseItem[] episodes)
        => _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.Episode) && q.IsPlayed == false && q.AncestorIds.Length == 1 && q.ParentIndexNumberNotEquals == 0)))
            .Returns(episodes.ToList().AsReadOnly());

    private void VerifyNoEpisodeQuery()
        => _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.Episode) && q.IsPlayed == false && q.AncestorIds.Length == 1 && q.ParentIndexNumberNotEquals == 0)), Times.Never);

    private (Request request, Context context, Entities.User user, SessionInfo session)
        CreatePlaybackNearlyFinishedContext(string tokenId)
    {
        var request = AlexaRequestFactory.CreateAudioPlayerRequest(
            "AudioPlayer.PlaybackNearlyFinished", tokenId, 0);

        var context = AlexaRequestFactory.CreateContextWithAudioPlayer(
            _userId.ToString(), DeviceId, tokenId, 0, "PLAYING");

        var user = new Entities.User { Id = _userId, JellyfinToken = "test-token" };
        var session = new SessionInfo(_sessionManagerMock.Object, _loggerFactory.CreateLogger<SessionInfo>());
        session.UserId = _userId;

        return (request, context, user, session);
    }
}

/// <summary>
/// Tests for PlaybackFinished behavior.
/// PlaybackFinished reports stop position, then ends or keeps alive based on queue state.
/// </summary>
[Collection("Plugin")]
public class PlaybackFinishedPostPlayTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Guid _userId = Guid.NewGuid();
    private const string DeviceId = "test-device";

    public PlaybackFinishedPostPlayTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "pf-postplay-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private PlaybackFinishedEventHandler CreateHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory);

    [Fact]
    public async Task QueueExhausted_EndsSession()
    {
        var handler = CreateHandler();
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackFinishedContext(itemId.ToString());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HasQueuedNext_KeepsAlive()
    {
        var handler = CreateHandler();
        var itemId = Guid.NewGuid();
        var (request, _, user, session) = CreatePlaybackFinishedContext(itemId.ToString());
        var context = AlexaRequestFactory.CreateContextWithAudioPlayer(
            _userId.ToString(), DeviceId, itemId.ToString(), 0, "PLAYING");

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Null(response.Response.ShouldEndSession);
    }

    private (Request request, Context context, Entities.User user, SessionInfo session)
        CreatePlaybackFinishedContext(string tokenId)
    {
        var request = AlexaRequestFactory.CreateAudioPlayerRequest(
            "AudioPlayer.PlaybackFinished", tokenId, 180000);

        var context = AlexaRequestFactory.CreateContextWithAudioPlayer(
            _userId.ToString(), DeviceId, tokenId, 180000, "FINISHED");

        var user = new Entities.User { Id = _userId, JellyfinToken = "test-token" };
        var session = new SessionInfo(_sessionManagerMock.Object, _loggerFactory.CreateLogger<SessionInfo>());
        session.UserId = _userId;

        return (request, context, user, session);
    }
}

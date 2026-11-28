using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

// COVERAGE POINTER (JF-808 gate-marker tail): the ShufflePlay WARMING gate twins
// (throw + ready, CreateShuffleHandler) live in PlayPlaylistIntentHandlerTests beside
// the playlist ask's twins (the shared-builder coverage family); grep here alone
// misses them. The two construction helpers can drift - check both when the
// handler's ctor changes.
// JF-974: the ALBUM branch's routing and gate pins live here (the branch is this
// handler's own dispatch decision, not shared-builder machinery).


[Collection("Plugin")]
public class ShufflePlayIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public ShufflePlayIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration { StreamProxyEnabled = false };
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private ShufflePlayIntentHandler CreateHandler(
        DeviceQueueManager? queueManager = null,
        IArtistIndex? artistIndex = null)
    {
        return new ShufflePlayIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory,
            queueManager,
            artistIndex);
    }

    private static IntentRequest CreateRequest(
        string intentName = IntentNames.ShufflePlay,
        string? playlistName = null,
        string? albumName = null,
        string locale = "en-US")
    {
        var slots = new Dictionary<string, Slot>();
        if (playlistName != null)
        {
            slots["playlist"] = new Slot { Name = "playlist", Value = playlistName };
        }

        if (albumName != null)
        {
            slots["album"] = new Slot { Name = "album", Value = albumName };
        }

        return new IntentRequest
        {
            Intent = new Intent { Name = intentName, Slots = slots },
            Locale = locale,
            RequestId = "test-req"
        };
    }

    /// <summary>
    /// The shared album fixture (hoisted to TestHelpers in the JF-974 simplify
    /// round; the second copy was the hoist point); the fixture's own contract
    /// lives on the TestHelpers helper.
    /// </summary>
    private (MusicAlbum Album, List<Audio> Tracks) SetupAlbum(string name = "thriller", int trackCount = 5)
        => TestHelpers.SetupAlbum(_libraryManagerMock, _userManagerMock, name, trackCount);

    [Fact]
    public void CanHandle_ShufflePlayIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "ShufflePlayIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "PlayPlaylistIntent" } };
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        Request request = new LaunchRequest();
        Assert.False(handler.CanHandle(request));
    }

    // -------------------------------------------------------------------------
    // JF-974: the album branch
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_AlbumSlotFilled_RoutesToTheAlbumShufflePath()
    {
        using var statics = TestHelpers.StubBaseItemStatics(_libraryManagerMock);
        var (_, tracks) = SetupAlbum();

        var context = TestHelpers.CreateTestContext("shuffle-album-branch");
        string deviceId = context.System.Device.DeviceID!;
        using var queueManager = TestHelpers.CreateDeviceQueueManager("shuffle-album-branch");
        var handler = CreateHandler(queueManager, TestHelpers.ReadyArtistIndex());
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, c => { }, "shuffle-album-branch");
        using var queueSwap = TestHelpers.SwapPluginQueueManager(queueManager);

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(albumName: "thriller"),
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        // The album path's observable contract: an AudioPlayer.Play of one of the
        // ALBUM's tracks and a committed shuffled device queue over the album's
        // full track list (a wrong routing to the playlist builder could produce
        // neither: no playlist fixture is installed).
        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        DeviceQueue q = queueManager.GetQueue(deviceId)!;
        Assert.NotNull(q);
        Assert.Equal("Shuffle", q.PlaybackOrder);
        Assert.Equal(tracks.Count, q.ItemIds.Count);
        Assert.Equal(new HashSet<string>(tracks.Select(t => t.Id.ToString())), new HashSet<string>(q.ItemIds));
        Guid launched = Guid.Parse(q.ItemIds[0]);
        Assert.Contains($"/Audio/{launched}/stream", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_PlaylistSlotFilled_KeepsThePlaylistPath()
    {
        using var statics = TestHelpers.StubBaseItemStatics(_libraryManagerMock);
        TestHelpers.SetupPlaylist(_libraryManagerMock, _userManagerMock);

        var context = TestHelpers.CreateTestContext("shuffle-playlist-branch");
        string deviceId = context.System.Device.DeviceID!;
        using var queueManager = TestHelpers.CreateDeviceQueueManager("shuffle-playlist-branch");
        var handler = CreateHandler(queueManager, TestHelpers.ReadyArtistIndex());
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, c => { }, "shuffle-playlist-branch");
        using var queueSwap = TestHelpers.SwapPluginQueueManager(queueManager);

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(playlistName: "road trip songs"),
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        // The playlist branch is today's path unchanged: shuffled queue over the
        // playlist's linked children (no album query is even installed here).
        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        DeviceQueue q = queueManager.GetQueue(deviceId)!;
        Assert.NotNull(q);
        Assert.Equal("Shuffle", q.PlaybackOrder);
        Assert.Equal(5, q.ItemIds.Count);
    }

    [Fact]
    public async Task HandleAsync_BothSlotsEmpty_ElicitsThePlaylistSlotNamingShufflePlay()
    {
        var handler = CreateHandler();

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        // JF-550: the elicit names the INVOKING intent; the updatedIntent declares
        // the full dialog slot set (playlist + the JF-974 album slot, the
        // ElicitSlots table entry).
        TestHelpers.AssertElicitsSlot(
            response,
            "playlist",
            IntentNames.ShufflePlay,
            new[] { "playlist", "album" });
    }

    [Fact]
    public async Task HandleAsync_AlbumSlotFilled_WhileIndexWarming_ThrowsAtEntry()
    {
        var handler = CreateHandler(artistIndex: TestHelpers.WarmingArtistIndex());
        using var statics = TestHelpers.StubBaseItemStatics(_libraryManagerMock);
        SetupAlbum();

        // The Layer-1 warming gate sits before the branch, so the album path's cold
        // surface (the album SearchTerm query and its fuzzy fallback) is gated like
        // the playlist path's.
        var ex = await Assert.ThrowsAsync<SkillWarmingUpException>(() =>
            handler.HandleAsync(
                CreateRequest(albumName: "thriller"),
                TestHelpers.CreateTestContext(),
                TestHelpers.CreateTestUser(jellyfinToken: "tok"),
                TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
                CancellationToken.None));
        Assert.StartsWith("artist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_AlbumSlotFilled_MusicDisabled_SpeaksMediaTypeNotAvailable()
    {
        var handler = CreateHandler(artistIndex: TestHelpers.ReadyArtistIndex());
        using var statics = TestHelpers.StubBaseItemStatics(_libraryManagerMock);
        SetupAlbum();

        // IfMediaTypeDisabled reads the LIVE Plugin.Instance configuration: ensure
        // one with music off (the fresh class _config stays on so the handler's own
        // collaborators are unaffected; the gate is what must observe the flag).
        var musicOff = new PluginConfiguration();
        TestHelpers.SetServerAddress(musicOff, "http://localhost:8096");
        musicOff.MusicEnabled = false;
        TestHelpers.EnsurePluginInstance(musicOff, _loggerFactory, c => { }, "shuffle-album-music-off");

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(albumName: "thriller"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        // The album branch is a music-only path (the PlayAlbum IfMediaTypeDisabled
        // contract): a disabled music library answers the media-type tell instead
        // of querying.
        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains(
            ResponseStrings.Get("MediaTypeNotAvailable", "en-US"),
            TestHelpers.GetSpeechText(response),
            StringComparison.OrdinalIgnoreCase);
    }
}

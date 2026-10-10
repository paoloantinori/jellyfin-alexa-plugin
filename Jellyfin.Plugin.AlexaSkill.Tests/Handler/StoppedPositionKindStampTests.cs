using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-812: the stop handler's position write stamps the stopped item's KIND
/// beside the position (<see cref="DeviceQueue.ItemPositionKinds"/>) so the
/// JF-797 deep-resume valve on the book head can skip song-shaped entries. The
/// stamps come from the ONE item resolution the save block makes
/// (<c>ILibraryManager.GetItemById</c>, the background event path), classified
/// through <c>AudiobookItems.IsAudioBookOrChapter</c>; an unresolved item
/// stores KINDLESS (the valve's conservative release).
/// </summary>
[Collection("Plugin")]
public class StoppedPositionKindStampTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "test-device";
    private const int OffsetMs = 364_118;

    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;
    private readonly Guid _jellyfinUserId = Guid.NewGuid();

    public StoppedPositionKindStampTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager("stopped-position-kind-stamp", _fx.LoggerFactory.CreateLogger<DeviceQueueManager>());
        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "stopped-position-kind-stamp");

        _fx.UserManager.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
    }

    public void Dispose()
    {
        _queueManager.Dispose();
        GC.SuppressFinalize(this);
    }

    private PlaybackStoppedEventHandler CreateHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LoggerFactory,
            _queueManager,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object);

    private async Task StopItem(Guid itemId)
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.UserId = _jellyfinUserId;
        await CreateHandler().HandleAsync(
            new AudioPlayerRequest { Type = "AudioPlayer.PlaybackStopped", Token = itemId.ToString(), OffsetInMilliseconds = OffsetMs },
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);
    }

    /// <summary>
    /// An AudioBook stopped item (the chapter-leaf or single-file book shape)
    /// stamps Book, so the valve releases.
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_AudioBookStoppedItem_StampsBookKind()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id))
            .Returns(new AudioBook { Name = "The Hobbit", Id = id });

        await StopItem(id);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(DeviceQueueManager.PositionKindBook, queue.ItemPositionKinds[id.ToString("N")]);
        Assert.True(_queueManager.HasAnyBookShapedStoredPosition(DeviceId));
    }

    /// <summary>
    /// A plain resolved song stamps Other, so the valve skips it (the JF-812
    /// tightening: household song playback no longer permanently releases the
    /// book head's deep-resume fetch).
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_PlainSongStoppedItem_StampsOtherKind()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id))
            .Returns(new Audio { Name = "Morning", Id = id });

        await StopItem(id);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(DeviceQueueManager.PositionKindOther, queue.ItemPositionKinds[id.ToString("N")]);
        Assert.False(_queueManager.HasAnyBookShapedStoredPosition(DeviceId));
    }

    /// <summary>
    /// An item that does not resolve (no LibraryManager row for the stopped id)
    /// still writes the position but stores it KINDLESS: the entry keeps the
    /// valve's conservative release, never a song-shaped skip the failed lookup
    /// could not justify.
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_UnresolvableStoppedItem_StoresKindless()
    {
        var id = Guid.NewGuid();
        // No GetItemById setup: the mock answers null (the pre-JF-812 mock
        // reality, and the unresolved-production-row shape).

        await StopItem(id);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        string key = id.ToString("N");
        Assert.Equal((long)OffsetMs * TimeSpan.TicksPerMillisecond, queue.ItemPositionState[key]);
        Assert.DoesNotContain(key, queue.ItemPositionKinds.Keys);
        Assert.True(_queueManager.HasAnyBookShapedStoredPosition(DeviceId));
    }
}

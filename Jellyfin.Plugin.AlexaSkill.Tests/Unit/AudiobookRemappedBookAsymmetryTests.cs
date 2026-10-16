using System;
using System.Linq;
using System.Threading.Tasks;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-799: the remapped-book fresh-vs-resume asymmetry, probed on the mixed row
/// set and DECIDED (the fresh arm unified for book-chapter launches).
/// THE MIXED-TYPING ANSWER (how to test the shape the scanner cannot produce):
/// a books library's scanner types EVERY audio row AudioBook (live capture
/// 2026-10-12: both "Mixed Rows Book" rows, mp3 files included, are AudioBook),
/// so the mixed row set (AudioBook-typed plus Audio-typed rows under one
/// verified book folder) exists only as METADATA STATE (rows remapped after
/// typing, e.g. re-imported under a different library or hand-retyped) and no
/// stable live fixture can hold it without re-typing the library. The probe
/// therefore MOCKS the hierarchy (the JF-798 AudiobookMultipartBookAltitudeTests
/// precedent): verbatim live folder/row ids and paths, with row two constructed
/// as the Audio-typed remap OF ITSELF (same id, same path, type changed): the
/// one honest documentation of the shape, since the remap changes the type and
/// nothing else.
/// THE PROBE OUTCOMES (pre-unification tree, all reproducible from this shape):
/// the fresh arm served the remapped row as a SINGLE-ITEM stream (the
/// AudioBook-gated climb drops Audio rows) while the same row's tracked resume
/// served the WHOLE-BOOK concat (the type-agnostic climb accepts it): one
/// book, one flag, two answers, depending on which row FindResumeTrackIndex
/// lands on. The write gate makes the hazard exactly the task's corrected
/// reachability: concat serves write folder-keyed book-timeline marks
/// (VideoAudioControllerTests' GetSegment_FolderBackedItem_RecordsPositionTracking,
/// row-type-blind by construction) while an Audio-typed leaf's single-item
/// serves record NOTHING (the gate's leaf branch requires an AudioBook leaf;
/// the album-track sibling test GetSegment of the JF-694 family pins a plain
/// Audio leaf recording nothing); so only the AudioBook rows' listening armed
/// the key the type-agnostic resume then sliced, at the concat position rather
/// than the user's last (untracked) position.
/// THE DECISION (JF-799): unify the FRESH arm type-agnostically for
/// BOOK-CHAPTER LAUNCHES ONLY (BuildAudiobookVideoAppLaunchResponseAsync threads
/// the launch context; BuildVideoAppAudioResponse's default arm keeps the
/// AudioBook gate). The music-safe prerequisite is satisfied BY
/// CONSTRUCTION: the caller's intent is the discriminator, no parent-shape
/// heuristic: a MusicAlbum/Playlist deny-list still passes plain music folders
/// holding loose tracks, and a books-library CollectionType walk costs an
/// ancestry query per launch, while the intent flag is free and cannot misfire
/// on music that never reaches the book composition. For AudioBook rows the
/// two climbs are identical by construction, so only remapped rows change.
/// The resume arm stays type-agnostic (the alternative deletes the remapped
/// book's seek-bar resume the JF-784 concat endpoint exists to serve). The
/// flow-level pins live in PlayBookIntentHandlerTests (the tracked-resume pin
/// plus its fresh-arm twin); the residuals (handler-level AudioBook gates at
/// ResumeIntent/StartOver entry) are recorded in the JF-799 task record.
/// In the Plugin collection like its JF-794/JF-798 siblings (the builder pins
/// ride the shared builder/plugin seams).
/// </summary>
[Collection("Plugin")]
public class AudiobookRemappedBookAsymmetryTests
{
    /// <summary>
    /// The mixed row set at the live "Mixed Rows Book" fixture (2026-10-12
    /// capture, folder and row ids verbatim): row one as scanned (AudioBook),
    /// row two as its own metadata remap (the same verbatim id and path,
    /// Audio-typed (the shape the books-library scanner cannot produce; see the
    /// class doc)). Both rows sit directly inside the verified book folder.
    /// </summary>
    private static (AudioBook AudioBookRow, Audio RemappedRow, Folder Book, Mock<ILibraryManager> Library) MixedRowsBookShape()
    {
        Guid zzzId = Guid.Parse("254b5ee8cd9f91ca68f2f5fe465ce034");
        Guid bookId = Guid.Parse("8de03fd49c0f2f06b61902368bd524cd");
        const string bookPath = "/data/media/audiobook/ZZZ_DEVELOPMENT_TESTS/Mixed Rows Book";
        var book = new Folder { Name = "Mixed Rows Book", Id = bookId, Path = bookPath, ParentId = zzzId };
        var audioBookRow = new AudioBook
        {
            Name = "Mixed Rows Book",
            Id = Guid.Parse("b4c2bab50ab9f6f08d7295f0d1a555f3"),
            ParentId = bookId,
            Path = bookPath + "/Audio Book Row One.mp3",
            RunTimeTicks = TimeSpan.FromSeconds(30).Ticks
        };
        var remappedRow = new Audio
        {
            Name = "Mixed Rows Book",
            Id = Guid.Parse("566b6ffdf9ba2fa0f7042419074de887"),
            ParentId = bookId,
            Path = bookPath + "/Audio Book Row Two.mp3",
            RunTimeTicks = TimeSpan.FromSeconds(30).Ticks
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(bookId)).Returns(book);
        library.Setup(l => l.GetItemById(zzzId))
            .Returns(new Folder { Name = "ZZZ_DEVELOPMENT_TESTS", Id = zzzId, Path = "/data/media/audiobook/ZZZ_DEVELOPMENT_TESTS" });
        return (audioBookRow, remappedRow, book, library);
    }

    private static PlaybackLaunchBuilder CreatePlainBuilder()
        => TestHelpers.CreateLocalhostLaunchBuilder();

    // ---------------- The unified fresh arm (RED pre-JF-799) ----------------

    [Fact]
    public async Task FreshLaunch_BookComposition_RemappedAudioRow_ConcatsUnderBookFolderId()
    {
        // RED pre-JF-799: the book composition's fresh launch of the remapped
        // row served the single-item stream (no whole-book seek bar, one chapter
        // with no VideoApp continuation, and a serve the write gate never
        // records). Post-JF-799 the book-chapter launch context makes the climb
        // type-agnostic, agreeing with the tracked-resume arm and the JF-784
        // concat endpoint that serves both row kinds.
        var (_, remappedRow, book, library) = MixedRowsBookShape();
        var builder = CreatePlainBuilder();

        SkillResponse response = await builder.BuildAudiobookVideoAppLaunchResponseAsync(
            remappedRow.Id.ToString(),
            remappedRow,
            announce: new global::Alexa.NET.Response.PlainTextOutputSpeech("announce"),
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            TestHelpers.CreateContextWithVideoApp(),
            request: null,
            library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/audiobook/{book.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"alexaskill/api/video-audio/{remappedRow.Id}", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshLaunch_BookComposition_AudioBookRow_ConcatsUnderBookFolderId()
    {
        // The anchor: for an AudioBook row the gated and ungated climbs are
        // identical BY CONSTRUCTION, so the unification is byte-identical for
        // scanned rows; only the remapped rows change behavior.
        var (audioBookRow, _, book, library) = MixedRowsBookShape();
        var builder = CreatePlainBuilder();

        SkillResponse response = await builder.BuildAudiobookVideoAppLaunchResponseAsync(
            audioBookRow.Id.ToString(),
            audioBookRow,
            announce: new global::Alexa.NET.Response.PlainTextOutputSpeech("announce"),
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            TestHelpers.CreateContextWithVideoApp(),
            request: null,
            library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/audiobook/{book.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    // ---------------- The discriminator is the launch context, not the item ----------------

    [Fact]
    public void FreshLaunch_DefaultArm_RemappedAudioRow_StaysSingleItem()
    {
        // The SAME remapped item through the DEFAULT arm (the music-shaped
        // entries: the chokepoint's GetVideoAppForAudio delegation, the flat
        // book arm) still serves the single-item stream: the type-agnostic
        // climb is reserved to callers that vouch book-chapter intent, so a
        // remapped chapter launched as "a track" keeps the track-shaped serve.
        var (_, remappedRow, book, library) = MixedRowsBookShape();
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildVideoAppAudioResponse(
            remappedRow.Id.ToString(),
            remappedRow,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            context: TestHelpers.CreateContextWithVideoApp(),
            libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/{remappedRow.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"audiobook/{book.Id}", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshLaunch_DefaultArm_PlainSongInsideMusicAlbum_NeverConcatsTheAlbum()
    {
        // The music-safety pin for the default arm: a plain song sits directly
        // inside its MusicAlbum (a Folder), exactly the shape an ungated climb
        // would album-concat through the audiobook URL/tracker machinery: the
        // hazard that made the fresh arm AudioBook-gated until JF-799. The
        // default arm must keep serving it as its own single item.
        Guid albumId = Guid.NewGuid();
        var album = new MusicAlbum { Name = "Greatest Hits", Id = albumId, Path = "/music/greatest-hits" };
        var song = new Audio
        {
            Name = "Song",
            Id = Guid.NewGuid(),
            ParentId = albumId,
            Path = "/music/greatest-hits/song.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(3).Ticks
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(albumId)).Returns(album);
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildVideoAppAudioResponse(
            song.Id.ToString(),
            song,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            context: TestHelpers.CreateContextWithVideoApp(),
            libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/{song.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("audiobook/", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    // ---------------- The ledger verdict (JF-894) ----------------

    [Fact]
    public async Task FreshLaunch_BookComposition_RemappedAudioRow_RecordsBookRouteInLedger_JF894()
    {
        // The launch site KNOWS the launch is a book (it walked the verified
        // book folder to build the concat), so the ledger records the book
        // verdict beside the chapter id. The bare VideoApp route could not carry
        // this: the remapped row's kind is seek-mode-music-shaped, so the
        // classification kernel read the entry as VideoAppAudio and voice pause
        // over the book spoke the music stopped line (the JF-894 bug).
        var (_, remappedRow, book, library) = MixedRowsBookShape();
        var builder = CreatePlainBuilder();
        var ledger = TestHelpers.CreateDeviceQueueManager("jf894-mint-book");
        using var pluginLedger = TestHelpers.SwapPluginLedgerScope(
            new PluginConfiguration(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            ledger,
            "jf894-mint-book");

        SkillResponse response = await builder.BuildAudiobookVideoAppLaunchResponseAsync(
            remappedRow.Id.ToString(),
            remappedRow,
            announce: new global::Alexa.NET.Response.PlainTextOutputSpeech("announce"),
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            TestHelpers.CreateContextWithVideoApp(),
            request: null,
            library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/audiobook/{book.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        (string? itemId, DeviceQueueManager.LaunchRoute? route, _) = ledger.GetLastPlayedSnapshot("echo-show");
        Assert.Equal(remappedRow.Id.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoAppAudiobook, route);
    }

    [Fact]
    public void FreshLaunch_DefaultArm_PlainSongInsideMusicAlbum_KeepsBareVideoAppRoute_JF894()
    {
        // The music-safety twin of the mint pin: a default-arm song launch (the
        // chokepoint's GetVideoAppForAudio delegation shape, including a song
        // sitting directly inside its MusicAlbum, which the climb would accept
        // but the music-safe gate refuses) records the bare VideoApp route, so
        // the classification kernel keeps answering VideoAppAudio for music
        // exactly as today. Only the verified book-concat arm mints the verdict.
        Guid albumId = Guid.NewGuid();
        var album = new MusicAlbum { Name = "Greatest Hits", Id = albumId, Path = "/music/greatest-hits" };
        var song = new Audio
        {
            Name = "Song",
            Id = Guid.NewGuid(),
            ParentId = albumId,
            Path = "/music/greatest-hits/song.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(3).Ticks
        };
        var library = new Mock<ILibraryManager>();
        // No parentage mock: the classifier deliberately never climbs (the route
        // literal answers first), so the MusicAlbum parent here is documentation of
        // the music shape, not a consulted dependency.
        var builder = CreatePlainBuilder();
        var ledger = TestHelpers.CreateDeviceQueueManager("jf894-mint-song");
        using var pluginLedger = TestHelpers.SwapPluginLedgerScope(
            new PluginConfiguration(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            ledger,
            "jf894-mint-song");

        SkillResponse response = builder.BuildVideoAppAudioResponse(
            song.Id.ToString(),
            song,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            context: TestHelpers.CreateContextWithVideoApp(),
            libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/{song.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        (string? itemId, DeviceQueueManager.LaunchRoute? route, _) = ledger.GetLastPlayedSnapshot("echo-show");
        Assert.Equal(song.Id.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, route);
    }

    // ---------------- Key agreement: where the slice lands ----------------

    [Fact]
    public void ResolveTrackedBookKey_MixedRows_BothRowTypesKeyTheSameBookFolder()
    {
        // The read/write coherence leg of the probe: the AudioBook row's concat
        // serves write the folder key (the write gate's folder branch), and BOTH
        // row types resolve that SAME key on the read side, so the resume slice
        // lands on the very timeline the marks were written on, whichever row
        // the ask lands on. This is what makes the tracked-resume pin's
        // whole-book slice correct for the mixed set.
        var (audioBookRow, remappedRow, book, library) = MixedRowsBookShape();

        Assert.Equal(book.Id.ToString("N"), AudiobookItems.ResolveTrackedBookKey(audioBookRow, library.Object));
        Assert.Equal(book.Id.ToString("N"), AudiobookItems.ResolveTrackedBookKey(remappedRow, library.Object));
    }
}

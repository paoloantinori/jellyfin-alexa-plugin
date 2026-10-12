using System;
using System.Linq;
using global::Alexa.NET.Request;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-798: the multi-part book altitude. The ONE verified climb
/// (<see cref="AudiobookItems.TryResolveVerifiedParentFolder"/>, shared by the
/// PlayBook-side <see cref="AudiobookItems.TryResolveBookFolder"/> and the JF-794
/// VideoApp builders) resolved the PART folder for a multi-part book laid out as
/// <c>Book/Part1/*.mp3</c>, <c>Book/Part2/*.mp3</c>: head, confirm, continuation,
/// the concat URL, and the tracker keys all stopped at the part boundary. The live
/// fixture (2026-10-11 capture, ZZZ_DEVELOPMENT_TESTS/Multipart Book, Part1 with 3
/// chapters + Part2 with 2, every chapter leaf an AudioBook whose ParentId is its
/// PART folder and whose file sits directly inside it) pinned the observed shape.
/// The fix is the outermost-book-ancestor walk ON the same seam: from the
/// one-level-verified parent, climb while the current folder is PART-LIKE
/// (anchored part-marker name, the only local discriminator that separates a part
/// subfolder from a book folder, see the seam's doc) AND the next ancestor verifies
/// by path. The JF-793 shared-container rejection must NOT weaken: the walk only
/// EXTENDS an accepted one-level verdict, so a rejected climb stays rejected.
/// Direction-2 pins: the census collapsed books (climb rejected, walk never runs),
/// the Think Again multi-file book (7 part-NAMED chapter FILES directly inside the
/// book folder: the book folder is not part-like, no climb), and the Mixed Rows
/// fixture (a plain multi-file book folder under a parent folder: no climb).
/// In the Plugin collection like its JF-794 sibling (the builder pins ride the
/// shared builder/plugin seams).
/// </summary>
[Collection("Plugin")]
public class AudiobookMultipartBookAltitudeTests
{
    /// <summary>
    /// The live fixture shape (2026-10-11 capture, ids verbatim): ZZZ_DEVELOPMENT_TESTS
    /// / Multipart Book / Part1+Part2, chapter leaves under the parts.
    /// </summary>
    private static (AudioBook Chapter, Folder Part1, Folder Part2, Folder Book, Mock<ILibraryManager> Library) MultipartBookShape()
    {
        Guid bookId = Guid.Parse("8e4ac6e230d3db369bfb4b61a3a2bd49");
        Guid part1Id = Guid.Parse("6be7caf6f9d37784f495a262338b52f5");
        Guid part2Id = Guid.Parse("25c3e48918a2337415374f636d81bbe3");
        const string bookPath = "/data/media/audiobook/ZZZ_DEVELOPMENT_TESTS/Multipart Book";
        var book = new Folder { Name = "Multipart Book", Id = bookId, Path = bookPath };
        var part1 = new Folder { Name = "Part1", Id = part1Id, Path = bookPath + "/Part1", ParentId = bookId };
        var part2 = new Folder { Name = "Part2", Id = part2Id, Path = bookPath + "/Part2", ParentId = bookId };
        var chapter = new AudioBook
        {
            Name = "Part1",
            Id = Guid.Parse("4628b7bd7a0cf555359b3ba502b58b56"),
            ParentId = part1Id,
            Path = bookPath + "/Part1/Chapter One.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(part1Id)).Returns(part1);
        library.Setup(l => l.GetItemById(part2Id)).Returns(part2);
        library.Setup(l => l.GetItemById(bookId)).Returns(book);
        return (chapter, part1, part2, book, library);
    }

    /// <summary>The live "Adam Grant - Think Again" shape: a multi-file BOOK folder
    /// whose 7 chapter FILES (part-named: Think Again-Part01.mp3 ...) sit DIRECTLY
    /// inside it, under the shared "Audiobooks" container.</summary>
    private static (AudioBook Chapter, Folder Book, Folder Container, Mock<ILibraryManager> Library) PartNamedFilesBookShape()
    {
        Guid containerId = Guid.Parse("bf81ecb676634231b819ce37c9a16800");
        Guid bookId = Guid.Parse("41e23cd6aee61919802452ba66eaa991");
        var container = new Folder { Name = "Audiobooks", Id = containerId, Path = "/data/media/audiobook/Audiobooks" };
        var book = new Folder { Name = "Adam Grant - Think Again", Id = bookId, Path = "/data/media/audiobook/Audiobooks/Adam Grant - Think Again", ParentId = containerId };
        var chapter = new AudioBook
        {
            Name = "Think Again - Part 1",
            Id = Guid.Parse("a93145ab92f80ec7d9ed2ba5d29e05de"),
            ParentId = bookId,
            Path = "/data/media/audiobook/Audiobooks/Adam Grant - Think Again/Think Again-Part01.mp3"
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(bookId)).Returns(book);
        library.Setup(l => l.GetItemById(containerId)).Returns(container);
        return (chapter, book, container, library);
    }

    /// <summary>The live "Mixed Rows Book" fixture: a plain multi-file book folder
    /// (2 AudioBook leaves directly inside) under the ZZZ_DEVELOPMENT_TESTS folder.</summary>
    private static (AudioBook Chapter, Folder Book, Folder Parent, Mock<ILibraryManager> Library) MultiFileBookFolderShape()
    {
        Guid parentId = Guid.Parse("254b5ee8cd9f91ca68f2f5fe465ce034");
        Guid bookId = Guid.Parse("8de03fd49c0f2f06b61902368bd524cd");
        var parent = new Folder { Name = "ZZZ_DEVELOPMENT_TESTS", Id = parentId, Path = "/data/media/audiobook/ZZZ_DEVELOPMENT_TESTS" };
        var book = new Folder { Name = "Mixed Rows Book", Id = bookId, Path = "/data/media/audiobook/ZZZ_DEVELOPMENT_TESTS/Mixed Rows Book", ParentId = parentId };
        var chapter = new AudioBook
        {
            Name = "Mixed Rows Book",
            Id = Guid.Parse("566b6ffdf9ba2fa0f7042419074de887"),
            ParentId = bookId,
            Path = "/data/media/audiobook/ZZZ_DEVELOPMENT_TESTS/Mixed Rows Book/Audio Book Row Two.mp3"
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(bookId)).Returns(book);
        library.Setup(l => l.GetItemById(parentId)).Returns(parent);
        return (chapter, book, parent, library);
    }

    /// <summary>The census collapsed single-file book: the ONE shared fixture
    /// (<see cref="TestHelpers.CreateCollapsedBookUnderContainer"/>).</summary>
    private static (AudioBook Book, Folder Container, Mock<ILibraryManager> Library) CollapsedBookShape()
        => TestHelpers.CreateCollapsedBookUnderContainer();

    private static PlaybackLaunchBuilder CreatePlainBuilder()
        => TestHelpers.CreateLocalhostLaunchBuilder();

    // ---------------- Direction 1: the multi-part chapter climbs to the BOOK ----------------

    [Fact]
    public void TryResolveBookFolder_MultipartChapterLeaf_ResolvesOutermostBookFolder()
    {
        // RED pre-JF-798: the one-level climb returned the PART folder.
        var (chapter, part1, _, book, library) = MultipartBookShape();

        Folder? resolved = AudiobookItems.TryResolveBookFolder(chapter, library.Object);

        Assert.NotNull(resolved);
        Assert.Equal(book.Id, resolved!.Id);
        Assert.NotEqual(part1.Id, resolved.Id);
    }

    [Fact]
    public void TryResolveBookFolder_Batched_MultipartChapterLeaf_ResolvesOutermostBookFolder()
    {
        // The batched scratchpad twin must agree BY CONSTRUCTION: the walk threads
        // the caller's per-ask ParentId cache through its own hops too.
        var (chapter, _, _, book, library) = MultipartBookShape();
        var cache = new System.Collections.Generic.Dictionary<Guid, Folder?>();

        Folder? resolved = AudiobookItems.TryResolveBookFolder(chapter, library.Object, cache);

        Assert.NotNull(resolved);
        Assert.Equal(book.Id, resolved!.Id);
    }

    [Fact]
    public void ResolveTrackedBookKey_MultipartChapterLeaf_KeysUnderBookId()
    {
        // The tracker-key coherence pin (JF-794 blocker 2, extended to the
        // outermost id): the write gate and the reads must agree on the BOOK key,
        // not one key per part.
        var (chapter, part1, _, book, library) = MultipartBookShape();

        string key = AudiobookItems.ResolveTrackedBookKey(chapter, library.Object);

        Assert.Equal(book.Id.ToString("N"), key);
        Assert.NotEqual(part1.Id.ToString("N"), key);
    }

    [Fact]
    public void FreshLaunch_MultipartChapterLeaf_ConcatsUnderBookFolderId()
    {
        // RED pre-JF-798: the fresh VideoApp launch minted the PART concat
        // (audiobook/{part1Id}); the seek bar spanned one part.
        var (chapter, part1, _, book, library) = MultipartBookShape();
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildVideoAppAudioResponse(
            chapter.Id.ToString(), chapter, TestHelpers.CreateTestUser(jellyfinToken: "tok"), context: TestHelpers.CreateContextWithVideoApp(), libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/audiobook/{book.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"audiobook/{part1.Id}", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackedResume_MultipartChapterLeaf_SlicesUnderBookFolderId()
    {
        // RED pre-JF-798: the tracked resume sliced the PART playlist; a position
        // earned in Part1 did not carry into a Part2 listen (and vice versa).
        var (chapter, part1, _, book, library) = MultipartBookShape();
        long startTicks = TimeSpan.FromMinutes(5).Ticks;
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildAudiobookResumeResponse(
            chapter, startTicks, TestHelpers.CreateTestUser(jellyfinToken: "tok"), TestHelpers.CreateContextWithVideoApp(), library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/audiobook/{book.Id}/stream.m3u8?start={startTicks}", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"audiobook/{part1.Id}", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Climb_NestedPartFolders_WalksToOutermostBook()
    {
        // Book/Part1/CD1/*.mp3: the walk continues over CONSECUTIVE part-like
        // folders and stops at the first non-marker ancestor (the book).
        Guid bookId = Guid.NewGuid();
        Guid partId = Guid.NewGuid();
        Guid cdId = Guid.NewGuid();
        var book = new Folder { Name = "Multipart Book", Id = bookId, Path = "/audiobooks/Multipart Book" };
        var part = new Folder { Name = "Part1", Id = partId, Path = "/audiobooks/Multipart Book/Part1", ParentId = bookId };
        var cd = new Folder { Name = "CD1", Id = cdId, Path = "/audiobooks/Multipart Book/Part1/CD1", ParentId = partId };
        var chapter = new AudioBook { Name = "CD1", Id = Guid.NewGuid(), ParentId = cdId, Path = "/audiobooks/Multipart Book/Part1/CD1/ch01.mp3" };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(cdId)).Returns(cd);
        library.Setup(l => l.GetItemById(partId)).Returns(part);
        library.Setup(l => l.GetItemById(bookId)).Returns(book);

        Folder? resolved = AudiobookItems.TryResolveBookFolder(chapter, library.Object);

        Assert.NotNull(resolved);
        Assert.Equal(bookId, resolved!.Id);
    }

    // ---------------- Direction 2: the shapes that must NOT climb ----------------

    [Fact]
    public void TryResolveBookFolder_CollapsedBookUnderSharedContainer_StillRejected()
    {
        // The JF-793 core rejection, seam-level guard: the collapsed leaf's climb
        // is rejected at ONE level and the walk never runs on a rejected verdict.
        var (book, container, library) = CollapsedBookShape();

        Assert.Null(AudiobookItems.TryResolveBookFolder(book, library.Object));
        Assert.Equal(book.Id.ToString("N"), AudiobookItems.ResolveTrackedBookKey(book, library.Object));
    }

    [Fact]
    public void Climb_PartNamedChapterFiles_MultiFileBook_StaysItsOwnFolder()
    {
        // The live Think Again shape: 7 part-NAMED chapter FILES sit directly
        // inside the book folder. The book folder itself is not part-like, so the
        // walk stops there; it must NOT climb into the shared container.
        var (chapter, book, container, library) = PartNamedFilesBookShape();

        Folder? resolved = AudiobookItems.TryResolveBookFolder(chapter, library.Object);

        Assert.NotNull(resolved);
        Assert.Equal(book.Id, resolved!.Id);
        Assert.NotEqual(container.Id, resolved.Id);
    }

    [Fact]
    public void ResolveTrackedBookKey_PartNamedFileBookLeaf_KeysUnderItsOwnBookFolder()
    {
        var (chapter, book, container, library) = PartNamedFilesBookShape();

        Assert.Equal(book.Id.ToString("N"), AudiobookItems.ResolveTrackedBookKey(chapter, library.Object));
        Assert.NotEqual(container.Id.ToString("N"), AudiobookItems.ResolveTrackedBookKey(chapter, library.Object));
    }

    [Fact]
    public void FreshLaunch_PartNamedFileBook_KeepsItsOwnBookFolderConcat()
    {
        // Builder-level direction-2 guard: the Think Again chapter keeps concating
        // under ITS book folder id, never the container id.
        var (chapter, book, container, library) = PartNamedFilesBookShape();
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildVideoAppAudioResponse(
            chapter.Id.ToString(), chapter, TestHelpers.CreateTestUser(jellyfinToken: "tok"), context: TestHelpers.CreateContextWithVideoApp(), libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/audiobook/{book.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"audiobook/{container.Id}", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Climb_PartFolder_DanglingAncestor_StopsAtThePart()
    {
        // The mid-walk fail-closed arm: the part folder is marker-named and
        // verified, but ITS ParentId dangles (metadata remap / partial import).
        // The walk breaks and the verdict stays the verified PART folder: never
        // null, never an exception shape.
        Guid partId = Guid.NewGuid();
        Guid danglingId = Guid.NewGuid();
        var part = new Folder { Name = "Part1", Id = partId, Path = "/audiobooks/Multipart Book/Part1", ParentId = danglingId };
        var chapter = new AudioBook { Name = "Part1", Id = Guid.NewGuid(), ParentId = partId, Path = "/audiobooks/Multipart Book/Part1/ch01.mp3" };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(partId)).Returns(part);

        Folder? resolved = AudiobookItems.TryResolveBookFolder(chapter, library.Object);

        Assert.NotNull(resolved);
        Assert.Equal(partId, resolved!.Id);
    }

    [Fact]
    public void Climb_MarkerNamedBookFolderUnderContainer_ClimbsToContainer_DocumentedResidual()
    {
        // The accepted residual (code-review F1, JF-798): a genuine BOOK folder
        // named a bare marker plus numeral, sitting under a shared container, is
        // structurally indistinguishable from a part subfolder and CLIMBS to the
        // container (the JF-793 hazard class). No query-free discriminator exists;
        // this pin holds the accepted behavior so a future fix flips it
        // deliberately, never silently.
        Guid containerId = Guid.NewGuid();
        Guid bookId = Guid.NewGuid();
        var container = new Folder { Name = "Audiobooks", Id = containerId, Path = "/audiobooks" };
        var volumeBook = new Folder { Name = "Volume 1", Id = bookId, Path = "/audiobooks/Volume 1", ParentId = containerId };
        var chapter = new AudioBook { Name = "Volume 1", Id = Guid.NewGuid(), ParentId = bookId, Path = "/audiobooks/Volume 1/ch01.mp3" };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(bookId)).Returns(volumeBook);
        library.Setup(l => l.GetItemById(containerId)).Returns(container);

        Folder? resolved = AudiobookItems.TryResolveBookFolder(chapter, library.Object);

        Assert.NotNull(resolved);
        Assert.Equal(containerId, resolved!.Id);
    }

    [Fact]
    public void Climb_PlainMultiFileBookFolder_NoClimbToParent()
    {
        // The live Mixed Rows Book fixture: a plain book folder under a parent
        // folder. Neither the book nor anything in the chain is part-like, so the
        // verdict is today's one-level answer, byte-identical.
        var (chapter, book, parent, library) = MultiFileBookFolderShape();

        Folder? resolved = AudiobookItems.TryResolveBookFolder(chapter, library.Object);

        Assert.NotNull(resolved);
        Assert.Equal(book.Id, resolved!.Id);
        Assert.NotEqual(parent.Id, resolved.Id);
    }
}

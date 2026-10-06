using System;
using System.Linq;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-794: the VideoApp builders' ParentId concat shares the ONE JF-793
/// shared-container discriminator. The live minix census (2026-10-06, recorded in
/// JF-793) proved the shape REAL: the "Audiobooks" library container directly
/// holds 6 collapsed single-file books, each an AudioBook leaf whose ParentId IS
/// the container, so the builders' raw climb minted
/// <c>audiobook/{containerId}</c> and the concat endpoint served every sibling
/// book as one timeline. The pins here drive the two builder sites
/// (<see cref="PlaybackLaunchBuilder.BuildVideoAppAudioResponse"/> and
/// <see cref="PlaybackLaunchBuilder.BuildAudiobookResumeResponse"/>) plus the
/// AudioPlayer chokepoint's native-controls delegation edge with THREE fixture
/// shapes: a VERIFIED chapter (its file sits directly inside the resolved book
/// folder: the concat must keep flowing, byte-identical to the raw climb since
/// the discriminator only ever rejects, never redirects), the CENSUS collapsed
/// book (file one directory deeper than the ParentId container: the single-item
/// stream / the flat AudioPlayer resume, never the container concat), and an
/// UNRESOLVABLE parent (a dangling ParentId: the leaf plays alone instead of the
/// dead <c>audiobook/{dangling}</c> URL the raw climb minted).
/// </summary>
public class PlaybackLaunchBuilderBookFolderDiscriminatorTests
{
    private static PlaybackLaunchBuilder CreatePlainBuilder()
        => TestHelpers.CreateLaunchBuilder(new PluginConfiguration { ServerAddress = "http://localhost:8096/" });

    /// <summary>
    /// The chokepoint pin's builder: the native-controls flag rides the CONFIG object
    /// (EnsurePluginInstance's syncFlag runs only when a shared instance already
    /// exists) because the chokepoint's delegation reads Plugin.Instance.Configuration.
    /// </summary>
    private static PlaybackLaunchBuilder CreateNativeControlsBuilder()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/", NativeControlsForBooks = true };
        TestHelpers.EnsurePluginInstance(config, LoggerFactory.Create(b => { }), c => c.NativeControlsForBooks = true, "jf794-builder-tests");
        return TestHelpers.CreateLaunchBuilder(config);
    }

    /// <summary>The flat multi-chapter shape rides the ONE shared verified fixture
    /// (<see cref="TestHelpers.CreateVerifiedBookChapter"/>): the chapter file sits
    /// DIRECTLY inside the resolved parent, so the parent IS the book folder.</summary>
    private static (AudioBook Chapter, Folder BookFolder, Mock<ILibraryManager> Library) VerifiedChapterShape(string name = "Chapter 1")
        => TestHelpers.CreateVerifiedBookChapter(name);

    /// <summary>The census shape: a collapsed single-file book whose ParentId is the
    /// SHARED container while its file sits inside its own subfolder below it.</summary>
    private static (AudioBook Book, Folder Container, Mock<ILibraryManager> Library) CollapsedBookShape()
    {
        Guid containerId = Guid.NewGuid();
        var book = new AudioBook
        {
            Name = "Managing Humans",
            Id = Guid.NewGuid(),
            ParentId = containerId,
            RunTimeTicks = TimeSpan.FromMinutes(20).Ticks,
            Path = "/audiobooks/Managing Humans/Managing Humans.m4b"
        };
        var container = new Folder { Name = "Audiobooks", Id = containerId, Path = "/audiobooks" };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(containerId)).Returns(container);
        return (book, container, library);
    }

    [Fact]
    public void FreshLaunch_VerifiedChapterFolder_ConcatsUnderFolderId()
    {
        // The flat-book true negative: the concat keeps flowing under the FOLDER id,
        // byte-identical to the pre-JF-794 raw climb (the discriminator accepts).
        var (chapter, folder, library) = VerifiedChapterShape();
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildVideoAppAudioResponse(
            chapter.Id.ToString(), chapter, TestHelpers.CreateTestUser(jellyfinToken: "tok"), context: TestHelpers.CreateContextWithVideoApp(), libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/audiobook/{folder.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"video-audio/{chapter.Id}", directive.VideoItem.Source, StringComparison.Ordinal);

        // The JF-694 tracker-key coherence: the URL's parent id is exactly the key
        // the segment requests record under and every resume read resolves through
        // ResumeMath.GetAudiobookBookKey, so the concat and the tracker cannot
        // disagree on the book they name.
        Assert.Equal(folder.Id, Guid.Parse(ResumeMath.GetAudiobookBookKey(chapter)));
    }

    [Fact]
    public void FreshLaunch_CollapsedBookUnderContainer_PlaysSingleItemUrl()
    {
        // RED on the unmodified tree: the raw climb minted the CONTAINER concat
        // (the whole-library merge); the discriminator must reject it and serve the
        // leaf's own single-item stream instead.
        var (book, container, library) = CollapsedBookShape();
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildVideoAppAudioResponse(
            book.Id.ToString(), book, TestHelpers.CreateTestUser(jellyfinToken: "tok"), context: TestHelpers.CreateContextWithVideoApp(), libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/{book.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"audiobook/{container.Id}", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshLaunch_UnresolvableParent_PlaysSingleItemUrl()
    {
        // RED on the unmodified tree: the raw climb minted audiobook/{dangling}, a
        // URL whose children query 404s; the verified climb degrades to the leaf.
        var chapter = new AudioBook
        {
            Name = "Chapter 01",
            Id = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
            Path = "/audiobooks/some-book/ch01.mp3"
        };
        var library = new Mock<ILibraryManager>();
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildVideoAppAudioResponse(
            chapter.Id.ToString(), chapter, TestHelpers.CreateTestUser(jellyfinToken: "tok"), context: TestHelpers.CreateContextWithVideoApp(), libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/{chapter.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("video-audio/audiobook/", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshLaunch_NoLibraryManager_FailsClosed_PlaysSingleItemUrl()
    {
        // The JF-793 gate-marker F1 direction, applied to the builders' seam: a
        // climb that CANNOT be verified (no library manager threaded; in production
        // every book-reachable site threads one) fails CLOSED to the leaf, never
        // the potentially-merged container.
        var (book, container, _) = CollapsedBookShape();
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildVideoAppAudioResponse(
            book.Id.ToString(), book, TestHelpers.CreateTestUser(jellyfinToken: "tok"), context: TestHelpers.CreateContextWithVideoApp());

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/{book.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"audiobook/{container.Id}", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackedResume_VerifiedChapterFolder_SlicesUnderFolderId()
    {
        var (chapter, folder, library) = VerifiedChapterShape();
        long startTicks = TimeSpan.FromMinutes(5).Ticks;
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildAudiobookResumeResponse(
            chapter, startTicks, TestHelpers.CreateTestUser(jellyfinToken: "tok"), TestHelpers.CreateContextWithVideoApp(), library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/audiobook/{folder.Id}/stream.m3u8?start={startTicks}", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.Equal(folder.Id, Guid.Parse(ResumeMath.GetAudiobookBookKey(chapter)));
    }

    [Fact]
    public void TrackedResume_CollapsedBookUnderContainer_DegradesToFlatAudioPlayerResume()
    {
        // RED on the unmodified tree: the raw climb minted the CONTAINER playlist
        // sliced at the book's tracker ticks (resuming the merged-library
        // timeline); the discriminator must reject the climb and degrade to the
        // flat chapter resume (the same shape the screenless branch serves), with
        // the tracked position clamped to the leaf's runtime.
        var (book, _, library) = CollapsedBookShape();
        long startTicks = TimeSpan.FromMinutes(5).Ticks;
        var builder = CreatePlainBuilder();

        SkillResponse response = builder.BuildAudiobookResumeResponse(
            book, startTicks, TestHelpers.CreateTestUser(jellyfinToken: "tok"), TestHelpers.CreateContextWithVideoApp(), library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Empty(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Equal(book.Id.ToString(), directive.AudioItem.Stream.Token);
        Assert.Equal((int)TimeSpan.FromMinutes(5).TotalMilliseconds, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public void ChokepointDelegation_VerifiedChapterFolder_StillConcats()
    {
        // The no-regression pin for the AudioPlayer chokepoint's native-controls
        // delegation edge: a LEGIT multi-chapter book chapter delegated from
        // BuildAudioPlayerResponse (flag on, capable device, offset 0) keeps the
        // concat launch.
        var (chapter, folder, library) = VerifiedChapterShape();
        var builder = CreateNativeControlsBuilder();

        SkillResponse response = builder.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            $"http://localhost:8096/Audio/{chapter.Id}/stream?static=true&api_key=tok",
            chapter.Id.ToString(),
            chapter,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            TestHelpers.CreateContextWithVideoApp(),
            libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/audiobook/{folder.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ChokepointDelegation_CollapsedBookUnderContainer_PlaysSingleItemUrl()
    {
        // RED on the unmodified tree: the delegation edge concat'd the container
        // for the census shape; the threaded manager must carry the discriminator
        // through the chokepoint too.
        var (book, container, library) = CollapsedBookShape();
        var builder = CreateNativeControlsBuilder();

        SkillResponse response = builder.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            $"http://localhost:8096/Audio/{book.Id}/stream?static=true&api_key=tok",
            book.Id.ToString(),
            book,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            TestHelpers.CreateContextWithVideoApp(),
            libraryManager: library.Object);

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"alexaskill/api/video-audio/{book.Id}/stream.m3u8", directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"audiobook/{container.Id}", directive.VideoItem.Source, StringComparison.Ordinal);
    }
}

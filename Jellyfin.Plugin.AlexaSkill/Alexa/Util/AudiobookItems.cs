using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The one replacement for the string-comparison audiobook type check (JF-567). MediaBrowser.Controller.Entities.AudioBook IS
/// in the controller package (since JF-584 every production site routes through this
/// wrapper; the bare <c>is</c> form lives only here), so the older
/// <c>GetType().Name.Equals("AudioBook", StringComparison.Ordinal)</c> string comparisons
/// were never needed; the type-pattern form also tracks subclasses of AudioBook, which
/// the exact string comparison silently dropped.
/// </summary>
internal static class AudiobookItems
{
    internal static bool IsAudioBook(BaseItem? item) => item is AudioBook;

    /// <summary>
    /// Resolve the BOOK FOLDER a matched audiobook search item belongs to (JF-791).
    /// Jellyfin never types a multi-file book folder as AudioBook (the AudioResolver
    /// skips multi-file directory collapsing, verified byte-identical at v10.11.8 and
    /// v12.2; AudioBook : Audio.Audio is a leaf class), so an AudioBook match for a
    /// multi-chapter book is a CHAPTER leaf whose ParentId is the plain Folder. This
    /// is the default AudioPlayer path's twin of the VideoApp builders' ParentId climb
    /// (<c>BuildVideoAppAudioResponse</c>/<c>BuildAudiobookResumeResponse</c>): the
    /// paged chapters machinery needs the folder's Id and Name, which a bare Guid
    /// cannot supply. Three deliberate divergences from that twin: this path VERIFIES
    /// the ParentId resolves to a Folder before adopting it (the builders concat the
    /// raw Guid, so a dangling ParentId degrades to the leaf play here but a dead URL
    /// there); the climb is ONE level, matching the builders' raw-ParentId semantics
    /// (a chapter under a subfolder resolves the subfolder on BOTH paths); and since
    /// JF-793 the climb is SHARED-CONTAINER-AWARE (the live minix census, 2026-10-06:
    /// the "Audiobooks" library container directly holds 6 collapsed single-file
    /// books, each an AudioBook leaf whose file sits one directory DEEPER than the
    /// container, because the resolver collapses single-file directories and hoists
    /// the book above its own folder): a leaf that does not sit DIRECTLY inside the
    /// resolved parent means the parent is a container of sibling books (or a part
    /// subfolder), not a book folder, and the climb is REJECTED so the leaf plays as
    /// its own track instead of merging the container into one queue. The builders'
    /// raw-ParentId concat does NOT share this discriminator (their sync is filed).
    /// A missing Path on either side cannot discriminate and defaults to the climb
    /// (the JF-791 shape; server-side folders always carry a Path). The single-file
    /// shapes (an AudioBook with an empty ParentId, or any non-AudioBook match), a
    /// failed folder resolution, and the shared-container rejection return null:
    /// callers keep the leaf shape and play it as its own track (the JF-361 duality),
    /// never a failed request.
    /// </summary>
    /// <param name="item">The audiobook search match (a chapter leaf or a single-file book).</param>
    /// <param name="libraryManager">The library manager resolving the ParentId.</param>
    /// <returns>The book folder, or null when the item is not a chapter leaf, the parent does not resolve to a Folder, or the parent is a shared container the leaf does not sit directly inside.</returns>
    internal static Folder? TryResolveBookFolder(BaseItem? item, ILibraryManager libraryManager)
    {
        if (item is not AudioBook chapter || chapter.ParentId == Guid.Empty)
        {
            return null;
        }

        if (libraryManager.GetItemById(chapter.ParentId) is not Folder folder)
        {
            return null;
        }

        return SitsDirectlyInside(chapter, folder) ? folder : null;
    }

    /// <summary>
    /// The JF-793 shared-container discriminator: whether the audio file sits directly
    /// inside the candidate book folder. A multi-chapter book's chapter files and an
    /// uncollapsed own-folder book's file both do; a COLLAPSED single-file book's file
    /// sits in its own subfolder below the shared container its ParentId names (the
    /// live census shape), and a chapter under a part subfolder likewise sits below
    /// the subfolder the one-level climb would resolve. Trailing separators are
    /// trimmed on both sides; the comparison is case-insensitive (Windows-hosted
    /// servers). Null or empty paths on either side cannot discriminate and return
    /// true (the pre-JF-793 unconditional climb, the documented default).
    /// </summary>
    private static bool SitsDirectlyInside(AudioBook chapter, Folder folder)
    {
        if (string.IsNullOrEmpty(chapter.Path) || string.IsNullOrEmpty(folder.Path))
        {
            return true;
        }

        string? chapterDirectory = System.IO.Path.GetDirectoryName(chapter.Path.TrimEnd('/', '\\'));
        return chapterDirectory != null
            && string.Equals(
                chapterDirectory.TrimEnd('/', '\\'),
                folder.Path.TrimEnd('/', '\\'),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the finished item is BOOK-shaped for the end-of-book decision (JF-670):
    /// an AudioBook itself, or an Audio item whose ancestry contains an AudioBook (the
    /// chapter and chapter-under-subfolder shapes). Two live-verified facts drive the
    /// shape: AudioBook DERIVES FROM Audio on both shipping refs (reflection-probed
    /// 10.11.8 and 12.0.0), so the radio paths' own <c>as Audio</c> casts do NOT no-op
    /// on a single-file book, it seeds genre radio like a song without this gate; and
    /// the initial chapters query is Recursive, so a chapter's direct parent can be a
    /// plain subfolder, hence the bounded ancestor walk. Detection reads the finished
    /// ITEM's own shape, so it survives the last continuation batch: the
    /// QueueContinuationStore entry is already removed at true end-of-book, but the
    /// item still names its book directly or via its ParentId chain.
    /// </summary>
    /// <param name="item">The finished item (null-safe: null returns false).</param>
    /// <param name="libraryManager">The library manager resolving the ancestor chain.</param>
    /// <returns>True when the item is an AudioBook or sits under one.</returns>
    internal static bool IsAudioBookOrChapter(BaseItem? item, ILibraryManager libraryManager)
    {
        // Item + three ancestors: book/chapter (one hop), book/subfolder/chapter
        // (two), and book/part/subfolder/chapter (three). The first-AudioBook early
        // exit keeps the common shapes at one lookup; a plain song burns at most its
        // album, folder, and library-root lookups on the gated path (radio-on or
        // PostPlay=AutoPlay exhaustion only). Live-shape note: the deepest book
        // layout in the test folder fixtures is the chapter directly under the book
        // (BrowseLibraryIntentHandlerTests' Audiobooks root); the deeper hops are
        // the defensive bound, not observed shapes.
        BaseItem? cursor = item;
        for (int hop = 0; cursor != null && hop < 4; hop++)
        {
            if (IsAudioBook(cursor))
            {
                return true;
            }

            cursor = cursor.ParentId != Guid.Empty
                ? libraryManager.GetItemById(cursor.ParentId)
                : null;
        }

        return false;
    }
}

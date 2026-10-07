using System;
using System.Collections.Generic;
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
    /// Resolve the BOOK FOLDER a matched audiobook SEARCH item belongs to (JF-791):
    /// the AudioBook-gated form of the ONE verified climb
    /// (<see cref="TryResolveVerifiedParentFolder"/>, whose doc owns the climb
    /// contract, the JF-793 shared-container story, and the JF-794 builder sync).
    /// Jellyfin never types a multi-file book folder as AudioBook (the AudioResolver
    /// skips multi-file directory collapsing, verified byte-identical at v10.11.8 and
    /// v12.2; AudioBook : Audio.Audio is a leaf class), so an AudioBook match for a
    /// multi-chapter book is a CHAPTER leaf whose ParentId is the plain Folder: the
    /// paged chapters machinery needs the folder's Id and Name, which a bare Guid
    /// cannot supply. The gate exists because the callers feed AUDIOBOOK SEARCH
    /// RESULTS (PlayBook's candidate normalization, the head climb, the YesIntent
    /// confirm climb); the builders use the ungated seam directly because their item
    /// argument can be an Audio-typed chapter. Null keeps the leaf shape so callers
    /// play it as its own track (the JF-361 duality), never a failed request.
    /// </summary>
    /// <param name="item">The audiobook search match (a chapter leaf or a single-file book).</param>
    /// <param name="libraryManager">The library manager resolving the ParentId; null fails closed (the seam's contract).</param>
    /// <returns>The book folder, or null when the item is not an AudioBook chapter leaf or the climb is rejected (see the seam's contract).</returns>
    internal static Folder? TryResolveBookFolder(BaseItem? item, ILibraryManager? libraryManager)
        => item is AudioBook ? TryResolveVerifiedParentFolder(item, libraryManager) : null;

    /// <summary>
    /// The batched form of <see cref="TryResolveBookFolder(BaseItem?, ILibraryManager?)"/> for
    /// the candidate normalization (JF-797 item 4, the lazy folder resolution):
    /// identical verdicts, but the ONE expensive step (the GetItemById parent
    /// fetch) runs once per DISTINCT ParentId through <paramref name="parentFolderCache"/>,
    /// not once per candidate. A short title whose search returns dozens of
    /// chapter leaves collapses onto a handful of books, so the per-candidate
    /// fetches were almost all repeats whose results evaporate at the fuzzy
    /// consumers. The cache maps ParentId to the RESOLVED Folder (null when the
    /// id resolves to no Folder); it is a per-ask scratchpad owned by the caller,
    /// never shared across requests (a library rescan between asks must not be
    /// served a stale folder). The per-candidate parts of the climb (the AudioBook
    /// gate, the empty-ParentId fail-closed, the SitsDirectlyInside path check)
    /// still run per candidate: two leaves sharing a ParentId can carry different
    /// paths (a metadata remap), and only the parent FETCH is ParentId-pure.
    /// </summary>
    /// <param name="item">The audiobook search candidate (a chapter leaf or a single-file book).</param>
    /// <param name="libraryManager">The library manager resolving unseen ParentIds; null fails closed (the seam's contract).</param>
    /// <param name="parentFolderCache">The caller's per-ask ParentId to resolved-Folder scratchpad (null values are cached misses).</param>
    /// <returns>The book folder, or null when the item is not an AudioBook chapter leaf or the climb is rejected (see the seam's contract).</returns>
    internal static Folder? TryResolveBookFolder(
        BaseItem? item,
        ILibraryManager? libraryManager,
        IDictionary<Guid, Folder?> parentFolderCache)
    {
        if (item is not AudioBook || item.ParentId == Guid.Empty || libraryManager is null)
        {
            return null;
        }

        if (!parentFolderCache.TryGetValue(item.ParentId, out Folder? parent))
        {
            parent = libraryManager.GetItemById(item.ParentId) as Folder;
            parentFolderCache.Add(item.ParentId, parent);
        }

        return parent != null && SitsDirectlyInside(item, parent) ? parent : null;
    }

    /// <summary>
    /// The ONE verified ParentId climb (JF-794), shared by the search-path twins
    /// (the <see cref="TryResolveBookFolder(BaseItem?, ILibraryManager?)"/> overloads,
    /// which add the AudioBook gate its search
    /// results carry) and the VideoApp builders' concat decision: resolve the leaf's
    /// ParentId to a Folder, then apply the JF-793 shared-container discriminator.
    /// Type-agnostic BY DESIGN on this seam: the builders' item argument can be an
    /// Audio-typed chapter of a metadata-remapped book (the JF-784 leg-3 row set),
    /// which the AudioBook gate of the search twin would drop. FAILS CLOSED on every
    /// unverifiable shape (a null library manager, an empty ParentId, a ParentId that
    /// resolves to no Folder, a leaf that does not sit directly inside the resolved
    /// folder): null, so every consumer degrades to the leaf playing as its own
    /// track, never a merged-container or dead-ParentId concat. When it ACCEPTS, the
    /// returned folder's Id is exactly the leaf's ParentId, so the discriminated
    /// concat URL is byte-identical to the pre-JF-794 raw climb.
    /// </summary>
    /// <param name="item">The audio leaf whose ParentId names the candidate book folder.</param>
    /// <param name="libraryManager">The library manager resolving the ParentId; null fails closed.</param>
    /// <returns>The verified book folder, or null when the climb cannot be verified or is rejected.</returns>
    internal static Folder? TryResolveVerifiedParentFolder(BaseItem? item, ILibraryManager? libraryManager)
    {
        if (item is null || item.ParentId == Guid.Empty || libraryManager is null)
        {
            return null;
        }

        if (libraryManager.GetItemById(item.ParentId) is not Folder folder)
        {
            return null;
        }

        return SitsDirectlyInside(item, folder) ? folder : null;
    }

    /// <summary>
    /// The ONE verdict-aware audiobook tracker key (JF-794 gate-marker blocker 2):
    /// the key the position tracker's WRITE gate and every resume READ/CLEAR site
    /// resolve through, so both sides apply the SAME discriminator verdict and a
    /// shared container can no longer blend sibling books into one key. Three
    /// outcomes, mirroring the climb: (1) the verified climb ACCEPTS, so the key is
    /// the book folder's id (identical to <see cref="ResumeMath.GetAudiobookBookKey"/>
    /// for a chapter leaf, kept as its own branch so the type-agnostic remap rows
    /// resolve the same folder the resume builder concats); (2) the leaf is an
    /// AudioBook whose ParentId RESOLVES to a Folder it does not sit directly
    /// inside (the census shared container, or a part subfolder), so the raw
    /// ParentId names a container of sibling books, not this book: the key is the
    /// leaf's OWN id, making each collapsed book's position independent; (3) every
    /// other shape (an empty ParentId, a dangling id, no manager): the raw
    /// <see cref="ResumeMath.GetAudiobookBookKey"/> value, which the write gate
    /// never arms (the root-book cold-key contract) so reads stay cold.
    /// COST NOTE (the per-segment question): the write gate runs on every segment
    /// fetch, and this helper adds one GetItemById(ParentId) (an in-memory platform
    /// LRU hit, the same lookup the gate already does for the leaf itself) plus the
    /// path compare; a plugin-side memo would only duplicate the platform cache and
    /// could serve a stale verdict across a library rescan, so it stays unmemoized
    /// like the gate's existing leaf lookup.
    /// </summary>
    /// <param name="item">The audiobook item (chapter leaf or single-file book; non-null).</param>
    /// <param name="libraryManager">The library manager resolving the ParentId; null falls to the raw-key outcome.</param>
    /// <returns>The tracker key the write gate and the reads must agree on.</returns>
    internal static string ResolveTrackedBookKey(BaseItem item, ILibraryManager? libraryManager)
    {
        if (TryResolveVerifiedParentFolder(item, libraryManager) is { } bookFolder)
        {
            return bookFolder.Id.ToString("N");
        }

        if (item is AudioBook
            && item.ParentId != Guid.Empty
            && libraryManager?.GetItemById(item.ParentId) is Folder)
        {
            return item.Id.ToString("N");
        }

        return ResumeMath.GetAudiobookBookKey(item);
    }

    /// <summary>
    /// The JF-793 shared-container discriminator: whether the audio file sits directly
    /// inside the candidate book folder. A multi-chapter book's chapter files and an
    /// uncollapsed own-folder book's file both do; a COLLAPSED single-file book's file
    /// sits in its own subfolder below the shared container its ParentId names (the
    /// live census shape), and a chapter under a part subfolder likewise sits below
    /// the subfolder the one-level climb would resolve. Trailing separators are
    /// trimmed on both sides; the comparison is case-insensitive (Windows-hosted
    /// servers). Null or empty paths on either side, and bare-filename leaf paths,
    /// cannot discriminate and return FALSE (the gate-marker tail F1: fail-CLOSED,
    /// not the earlier fail-open climb - a Path-less or bare-name leaf under a
    /// shared ParentId container is exactly the library-merge hazard finding 2
    /// closed, and an unverifiable layout plays the leaf alone, the pre-JF-791
    /// behavior, never the merged container). Widened from AudioBook to BaseItem at
    /// JF-794 when the builders joined this seam (see
    /// <see cref="TryResolveVerifiedParentFolder"/>); the body reads only the Path.
    /// </summary>
    private static bool SitsDirectlyInside(BaseItem chapter, Folder folder)
    {
        if (string.IsNullOrEmpty(chapter.Path) || string.IsNullOrEmpty(folder.Path))
        {
            return false;
        }

        // GetDirectoryName returns EMPTY (not null) for a bare filename; a bare
        // name cannot prove the file sits inside the folder, so it fails closed
        // with the other unverifiable shapes (gate-marker tail F1).
        string? chapterDirectory = System.IO.Path.GetDirectoryName(chapter.Path.TrimEnd('/', '\\'));
        return !string.IsNullOrEmpty(chapterDirectory)
            && string.Equals(
                chapterDirectory.TrimEnd('/', '\\'),
                folder.Path.TrimEnd('/', '\\'),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a confirmed "album"-labelled disambiguation payload is a BOOK
    /// (JF-793 code-review F1): an AudioBook item (the chapter-leaf and
    /// single-file payloads) or a plain book FOLDER (the payload shape the
    /// PlayBook candidate normalization emits for multi-chapter books; a Folder is
    /// not an AudioBook, so the bare <see cref="IsAudioBook"/> gate misrouted
    /// those confirms into the album leg). MusicAlbum stays excluded: PlayAlbum's
    /// own disambiguation matches are MusicAlbums and must keep routing to the
    /// album leg.
    /// JF-797 item 3 (the payload-kind gate): the Folder arm is narrowed by a
    /// KIND DENY-LIST (MusicArtist, MusicGenre, CollectionFolder, Playlist beside
    /// MusicAlbum). The two live producers emit only AudioBook leaves and plain
    /// book folders, so the breadth was latent, but any current-or-future
    /// producer emitting one of the denied kinds reached the PlayBook leg, whose
    /// climb answers null and whose chapters query returns zero children: a
    /// broken NoContentInBook launch (and, since JF-795, a FeatureDisabled Tell
    /// with books off) where the pre-JF-793 album leg answered such payloads.
    /// Denied kinds fall through to YesIntentHandler's album-leg switch arm, the
    /// pre-JF-793 behavior (MusicGenre is not even a Folder, so its entry is
    /// belt-and-braces documenting the kind). A positive children-are-AudioBook
    /// probe was evaluated and declined: it would cost a query on every real
    /// book-folder confirm to close a breadth no producer exhibits.
    /// </summary>
    internal static bool IsBookDisambiguationPayload(BaseItem? item)
        => item is AudioBook
           || (item is Folder
               && item is not MediaBrowser.Controller.Entities.Audio.MusicAlbum
               && item is not MediaBrowser.Controller.Entities.Audio.MusicArtist
               && item is not MediaBrowser.Controller.Entities.Audio.MusicGenre
               && item is not MediaBrowser.Controller.Entities.CollectionFolder
               && item is not MediaBrowser.Controller.Playlists.Playlist);

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

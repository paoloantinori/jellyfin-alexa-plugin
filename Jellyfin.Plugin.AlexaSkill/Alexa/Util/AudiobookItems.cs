using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The one replacement for the string-comparison audiobook type check (JF-567). MediaBrowser.Controller.Entities.AudioBook IS
/// in the controller package (since JF-584 every production site routes through this
/// wrapper; the bare <c>is</c> form lives only here), so the older
/// <c>GetType().Name.Equals("AudioBook", StringComparison.Ordinal)</c> string comparisons
/// were never needed; the type-pattern form also tracks subclasses of AudioBook, which
/// the exact string comparison silently dropped.
/// </summary>
internal static partial class AudiobookItems
{
    internal static bool IsAudioBook(BaseItem? item) => item is AudioBook;

    /// <summary>
    /// Resolve the BOOK FOLDER a matched audiobook SEARCH item belongs to (JF-791):
    /// the AudioBook-gated form of the ONE verified climb
    /// (<see cref="TryResolveVerifiedParentFolder(BaseItem?, ILibraryManager?)"/>, whose doc owns the climb
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
    /// identical verdicts BY CONSTRUCTION (the same ONE climb seam, whose parent
    /// FETCH resolves through the caller's scratchpad), with the ONE expensive
    /// step (the GetItemById parent fetch) running once per DISTINCT ParentId
    /// instead of once per candidate. A short title whose search returns dozens of
    /// chapter leaves collapses onto a handful of books, so the per-candidate
    /// fetches were almost all repeats whose results evaporate at the fuzzy
    /// consumers. The cache maps ParentId to the RESOLVED Folder (null when the
    /// id resolves to no Folder); it is a per-ask scratchpad owned by the caller,
    /// never shared across requests (a library rescan between asks must not be
    /// served a stale folder). The per-candidate parts of the climb (the AudioBook
    /// gate and the SitsDirectlyInside path check) still run per candidate inside
    /// the shared seam: two leaves sharing a ParentId can carry different paths (a
    /// metadata remap), and only the parent FETCH is ParentId-pure.
    /// </summary>
    /// <param name="item">The audiobook search candidate (a chapter leaf or a single-file book).</param>
    /// <param name="libraryManager">The library manager resolving unseen ParentIds; null fails closed (the seam's contract).</param>
    /// <param name="parentFolderCache">The caller's per-ask ParentId to resolved-Folder scratchpad (null values are cached misses).</param>
    /// <returns>The book folder, or null when the item is not an AudioBook chapter leaf or the climb is rejected (see the seam's contract).</returns>
    internal static Folder? TryResolveBookFolder(
        BaseItem? item,
        ILibraryManager? libraryManager,
        IDictionary<Guid, Folder?> parentFolderCache)
        => item is AudioBook ? TryResolveVerifiedParentFolder(item, libraryManager, parentFolderCache) : null;

    /// <summary>
    /// The ONE verified book-ancestor climb (JF-794 one-level verdict + the JF-798
    /// outermost-book-ancestor walk), shared by the search-path twins
    /// (the <see cref="TryResolveBookFolder(BaseItem?, ILibraryManager?)"/> overloads,
    /// which add the AudioBook gate its search
    /// results carry) and the VideoApp builders' concat decision: resolve the leaf's
    /// ParentId to a Folder, apply the JF-793 shared-container discriminator, then
    /// continue over part-marker subfolders to the OUTERMOST book ancestor.
    /// Type-agnostic BY DESIGN on this seam: the builders' item argument can be an
    /// Audio-typed chapter of a metadata-remapped book (the JF-784 leg-3 row set),
    /// which the AudioBook gate of the search twin would drop. FAILS CLOSED on every
    /// unverifiable shape (a null library manager, an empty ParentId, a ParentId that
    /// resolves to no Folder, a leaf that does not sit directly inside the resolved
    /// folder): null, so every consumer degrades to the leaf playing as its own
    /// track, never a merged-container or dead-ParentId concat. When it ACCEPTS, the
    /// returned folder is the OUTERMOST verified book ancestor: for the flat shapes
    /// (a chapter directly inside its book folder) that is still exactly the leaf's
    /// ParentId, so the pre-JF-798 accepted concat URL is byte-identical; for the
    /// multi-part shape (a leaf inside a Part subfolder inside the book) it is the
    /// BOOK folder, so head, confirm, continuation, the concat URL, and the tracker
    /// keys all span the whole book instead of stopping at the part boundary.
    /// The scratchpad overload (JF-797 item 4) threads the batched twin's per-ask
    /// cache into the SAME body, so the fail-closed contract stays ONE definition
    /// (the walk's own hops resolve through the same scratchpad).
    /// </summary>
    /// <param name="item">The audio leaf whose ParentId names the candidate book folder.</param>
    /// <param name="libraryManager">The library manager resolving the ParentId; null fails closed.</param>
    /// <returns>The verified outermost book ancestor, or null when the climb cannot be verified or is rejected.</returns>
    internal static Folder? TryResolveVerifiedParentFolder(BaseItem? item, ILibraryManager? libraryManager)
        => TryResolveVerifiedParentFolder(item, libraryManager, parentFolderCache: null);

    /// <summary>
    /// The scratchpad form of the ONE verified climb (see the two-arg overload's
    /// doc for the contract): identical verdicts, with the parent fetch resolved
    /// through <paramref name="parentFolderCache"/> when given.
    /// </summary>
    /// <param name="item">The audio leaf whose ParentId names the candidate book folder.</param>
    /// <param name="libraryManager">The library manager resolving unseen ParentIds; null fails closed.</param>
    /// <param name="parentFolderCache">The caller's per-ask ParentId to resolved-Folder scratchpad, or null for the uncached climb.</param>
    /// <returns>The verified book folder, or null when the climb cannot be verified or is rejected.</returns>
    internal static Folder? TryResolveVerifiedParentFolder(
        BaseItem? item,
        ILibraryManager? libraryManager,
        IDictionary<Guid, Folder?>? parentFolderCache)
    {
        if (item is null || item.ParentId == Guid.Empty || libraryManager is null)
        {
            return null;
        }

        if (ResolveParentFolder(item.ParentId, libraryManager, parentFolderCache) is not { } folder)
        {
            return null;
        }

        if (!SitsDirectlyInside(item, folder))
        {
            // The JF-793 shared-container rejection. The JF-798 walk NEVER runs on a
            // rejected verdict: it only EXTENDS an accepted one-level climb, so the
            // census collapsed books keep playing as their own tracks unchanged.
            return null;
        }

        // JF-798: the outermost-book-ancestor walk (the contract lives on the
        // method doc; IsPartLikeFolderName owns the discriminator choice). Each hop
        // re-verifies with the SAME SitsDirectlyInside path compare the base
        // verdict uses and re-uses the ONE scratchpad-aware ResolveParentFolder.
        Folder book = folder;
        for (int hop = 0; hop < MaxBookAncestorHops; hop++)
        {
            if (book.ParentId == Guid.Empty || !IsPartLikeFolderName(book.Name))
            {
                break;
            }

            if (ResolveParentFolder(book.ParentId, libraryManager, parentFolderCache) is not { } parent
                || !SitsDirectlyInside(book, parent))
            {
                break;
            }

            book = parent;
        }

        return book;
    }

    /// <summary>
    /// The climb's parent-fetch step, the only ParentId-pure part (JF-797 item 4):
    /// resolved through the caller's per-ask scratchpad when given (null values
    /// are cached misses, so a dangling id costs one lookup per ask), fetched
    /// directly otherwise.
    /// </summary>
    /// <param name="parentId">The leaf's ParentId.</param>
    /// <param name="libraryManager">The library manager resolving unseen ids.</param>
    /// <param name="parentFolderCache">The caller's scratchpad, or null.</param>
    /// <returns>The resolved folder, or null when the id resolves to nothing or to a non-folder.</returns>
    private static Folder? ResolveParentFolder(
        Guid parentId,
        ILibraryManager libraryManager,
        IDictionary<Guid, Folder?>? parentFolderCache)
    {
        if (parentFolderCache != null && parentFolderCache.TryGetValue(parentId, out Folder? cached))
        {
            return cached;
        }

        Folder? resolved = libraryManager.GetItemById(parentId) as Folder;
        parentFolderCache?.Add(parentId, resolved);
        return resolved;
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
    /// inside (the census shared container; a part subfolder stopped being this
    /// shape at JF-798, the walk carries it up to the book), so the raw
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
        => ResolveTrackedBookShape(item, libraryManager) switch
        {
            { Shape: TrackedBookShape.VerifiedFolder, Folder: { } bookFolder } => bookFolder.Id.ToString("N"),
            { Shape: TrackedBookShape.CollapsedLeaf } => item.Id.ToString("N"),
            _ => ResumeMath.GetAudiobookBookKey(item),
        };

    /// <summary>
    /// The ONE verdict-aware tracked-shape discriminator (JF-860): both the key
    /// resolver (<see cref="ResolveTrackedBookKey"/>, JF-794) and the
    /// timeline-identity resolver
    /// (<see cref="TryResolveTrackedTimelineIdentity"/>) derive from this shape,
    /// so a refinement of the discriminator can never land on one twin only and
    /// leave the key and the identity disagreeing (tests mock both resolvers to
    /// agree, so such a split would be invisible to the suite). The three shapes
    /// mirror <see cref="ResolveTrackedBookKey"/>'s documented outcomes: the
    /// verified climb's book folder, the collapsed single-file book's own leaf,
    /// and the cold-key shapes the write gate never arms.
    /// </summary>
    private enum TrackedBookShape
    {
        /// <summary>The verified climb accepts: the book folder's key/timeline.</summary>
        VerifiedFolder,

        /// <summary>An AudioBook leaf whose ParentId names a container of sibling books: the leaf's own key, the degenerate single-item timeline.</summary>
        CollapsedLeaf,

        /// <summary>Every other shape (empty ParentId, dangling id, no manager): the raw key, never written, no enumerable timeline.</summary>
        ColdKey,
    }

    /// <summary>
    /// Resolve the tracked shape of an audiobook item; see <see cref="TrackedBookShape"/>.
    /// The folder is non-null only on the VerifiedFolder shape.
    /// </summary>
    private static (TrackedBookShape Shape, Folder? Folder) ResolveTrackedBookShape(BaseItem item, ILibraryManager? libraryManager)
    {
        if (TryResolveVerifiedParentFolder(item, libraryManager) is { } bookFolder)
        {
            return (TrackedBookShape.VerifiedFolder, bookFolder);
        }

        if (item is AudioBook
            && item.ParentId != Guid.Empty
            && libraryManager?.GetItemById(item.ParentId) is Folder)
        {
            return (TrackedBookShape.CollapsedLeaf, null);
        }

        return (TrackedBookShape.ColdKey, null);
    }

    /// <summary>
    /// The timeline-identity twin of <see cref="ResolveTrackedBookKey"/> (JF-860,
    /// both derived from the ONE <see cref="ResolveTrackedBookShape"/> verdict):
    /// the timeline identity the tracker's identity-gated read
    /// (<see cref="AudiobookPositionTracker.GetPositionTicks(string, TimelineIdentity)"/>)
    /// arbitrates with, resolved from the CALLER's enumeration of the book - the
    /// best available proxy for the serving enumeration (the serve's own identity
    /// comes from its token-scoped enumeration; the residual when the two scopes
    /// diverge is documented on the gated read). The verified folder enumerates
    /// its scoped chapters (the row shape the concat endpoint serves); the
    /// collapsed leaf answers the degenerate single-item pair the record gate
    /// stamps; every cold-key shape answers null, and the caller keeps the
    /// identity-agnostic read (those keys never hold marks, so no divergence is
    /// possible there).
    /// COST CONTRACT: the folder arm is ONE unpaged scoped query - call it only
    /// behind a warm tracker key (the announce site reads the agnostic mark first
    /// and resolves the identity only when a position would be spoken), never on
    /// a per-segment or per-refresh path.
    /// </summary>
    /// <param name="item">The audiobook item the tracker key was resolved from (chapter leaf or single-file book).</param>
    /// <param name="libraryManager">The library manager resolving the shape; null falls to the cold-key outcome.</param>
    /// <param name="pluginUser">The plugin user whose library scope the enumeration applies (the serve's scope proxy).</param>
    /// <param name="logger">Logger for the triage line on a failed enumeration.</param>
    /// <returns>The timeline identity, or null when the shape carries no enumerable timeline (the caller keeps the agnostic read).</returns>
    internal static TimelineIdentity? TryResolveTrackedTimelineIdentity(
        BaseItem item,
        ILibraryManager? libraryManager,
        Entities.User? pluginUser,
        ILogger logger)
        => ResolveTrackedBookShape(item, libraryManager) switch
        {
            { Shape: TrackedBookShape.VerifiedFolder, Folder: { } bookFolder } => EnumerateBookTimelineIdentity(bookFolder, pluginUser, libraryManager!, logger),
            { Shape: TrackedBookShape.CollapsedLeaf } => TimelineIdentity.ForSingleItem(item),
            _ => null,
        };

    /// <summary>
    /// The folder arm of the identity resolver: the scoped unpaged chapters
    /// enumeration's (count, runtime sum) pair, through the ONE factory
    /// (<see cref="TimelineIdentity.FromChapters"/>).
    /// </summary>
    private static TimelineIdentity? EnumerateBookTimelineIdentity(
        Folder bookFolder,
        Entities.User? pluginUser,
        ILibraryManager libraryManager,
        ILogger logger)
    {
        try
        {
            // The user-scoped sibling of the endpoint's own enumeration (the
            // endpoint runs BuildAudiobookChaptersQueryUnpaged under the token
            // scope): the same MediaTypes=Audio row set and the same chapters
            // order, scoped to the plugin user instead of the token. Only the
            // count and the runtime sum are consumed, and RunTimeTicks hydrates
            // regardless of DtoOptions, so swap the builder's full-field row
            // shape for the cheap one (the JF-797 row-volume discipline).
            InternalItemsQuery query = QueueContinuationFetcher.BuildScopedAudiobookChaptersQueryUnpaged(
                jellyfinUser: null, pluginUser, libraryManager, logger, bookFolder.Id);
            query.DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(false) { EnableImages = false, EnableUserData = false, AddCurrentProgram = false };
            IReadOnlyList<BaseItem> chapters = libraryManager.GetItemList(query);
            return TimelineIdentity.FromChapters(chapters);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort identity (the gate is an announce refinement, never
            // a resume dependency): a failed enumeration falls back to the
            // caller's identity-agnostic read. RESIDUAL (accepted): that one
            // request can again announce a position the serve drops (the
            // pre-JF-860 divergence); the conservative alternative (read as
            // absent) would trade it for a lost resume. Both are transient and
            // the enumeration is not the request's first database touch, so a
            // failure here means the resume is already failing upstream.
            // Cancellation propagates (the ask is being torn down, not failing).
            logger.LogWarning(
                ex,
                "TryResolveTrackedTimelineIdentity: chapters enumeration failed for book folder {BookFolderId}; keeping the identity-agnostic tracker read",
                bookFolder.Id);
            return null;
        }
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
    /// <see cref="TryResolveVerifiedParentFolder(BaseItem?, ILibraryManager?)"/>); the body reads only the Path.
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
    /// The JF-798 walk's hop bound (the defensive depth cap, matching
    /// <see cref="IsAudioBookOrChapter"/>'s ancestor bound): the plausible layouts
    /// are Book/Part1 and Book/Part1/CD1, and a ParentId chain is acyclic in
    /// Jellyfin, so the cap exists to bound the walk, never to correct it.
    /// </summary>
    private const int MaxBookAncestorHops = 4;

    /// <summary>
    /// The JF-798 part-marker pattern: a folder NAME that is nothing but a
    /// part/disc/volume/chapter marker plus a numeral (Arabic 1-3 digits, a roman
    /// numeral, or a single a-d side letter). Anchored case-insensitive; the
    /// numeral is REQUIRED, which is what keeps real book titles out ("Multipart
    /// Book", "The Parting", "Thinking Better" all fail the anchor or the numeral).
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(part|parte|partie|teil|chap|chapter|capitolo|kapitel|cd|disc|disk|disco|disque|vol|volume|side|track|traccia)[\s._-]*(\d{1,3}|[ivxl]{1,3}|[a-d])$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PartFolderNamePattern();

    /// <summary>
    /// The JF-798 walk's stop-condition discriminator: whether a folder is a
    /// PART-LIKE subfolder of something (a bare part/disc/volume/chapter marker
    /// name), which is the ONLY signal the climb reads when deciding to extend
    /// upward. CHOSEN over the task's other two candidates because it is the only
    /// zero-extra-query option on the per-segment write-gate path AND the only one
    /// with discriminative power on the live library: Jellyfin types nothing (the
    /// library root, the ZZZ folder, the book, and the part are ALL plain Folders,
    /// so the library-root/CollectionType probe cannot even identify WHICH level
    /// below the root is the book), and the census container's single-file book
    /// subfolders are structurally IDENTICAL to a Book/Part1 pair, so folder-content
    /// heuristics cannot separate "part of a book" from "one of many books" without
    /// a children query per hop. RESIDUAL (accepted, and pinned as the accepted
    /// behavior in AudiobookMultipartBookAltitudeTests): a genuine BOOK folder named
    /// a bare marker plus numeral ("Volume 1" holding its chapters directly, under
    /// a shared container) is structurally indistinguishable from a part subfolder
    /// and WOULD climb, and the harm is the FULL JF-793 hazard class, not a benign
    /// merged timeline: the returned container drives a Recursive concat playlist
    /// of every sibling book AND ResolveTrackedBookKey blends the siblings' marks
    /// under one key. No query-free discriminator separates the two shapes; none
    /// such name exists in the live library (2026-10-11 capture); if one appears,
    /// the pin flips the design deliberately. The numeral classes are also
    /// deliberately narrow: spelled-out numerals ("Part One", "Chapitre 2") do NOT
    /// match, so such libraries keep the part-boundary behavior; extend the
    /// pattern per observed locale evidence, never speculatively (this line is the
    /// pattern's source of truth, there is no separate locale table).
    /// Composite part names ("Part 1 - The Beginning") likewise do NOT match (the
    /// anchor excludes free text); such a library keeps the part-boundary
    /// behavior, never a wrong merge.
    /// </summary>
    /// <param name="name">The candidate folder's name, or null.</param>
    /// <returns>True when the name is a bare part/disc/volume/chapter marker.</returns>
    private static bool IsPartLikeFolderName(string? name)
        => !string.IsNullOrWhiteSpace(name) && PartFolderNamePattern().IsMatch(name);

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
    /// KIND DENY-LIST that is EXHAUSTIVE over the controller refs' concrete
    /// Folder subclasses (reflection-enumerated, byte-identical sets at 10.11.8
    /// and 12.0.0: MusicAlbum, MusicArtist, CollectionFolder, UserRootFolder,
    /// UserView, AggregateFolder, PhotoAlbum, BoxSet, Season, Series, Playlist,
    /// Channel; MusicGenre is not even a Folder, its entry is belt-and-braces
    /// documenting the kind), so ONLY a plain Folder (the book-folder payload
    /// shape the PlayBook candidate normalization emits) or an AudioBook passes.
    /// The two live producers emit only AudioBook leaves and plain book folders,
    /// so the breadth was latent, but any current-or-future producer emitting one
    /// of the denied kinds reached the PlayBook leg, whose climb answers null and
    /// whose chapters query returns zero children: a broken NoContentInBook
    /// launch (and, since JF-795, a FeatureDisabled Tell with books off) where
    /// the pre-JF-793 album leg answered such payloads. Denied kinds fall through
    /// to YesIntentHandler's album-leg switch arm, the pre-JF-793 behavior. A
    /// positive children-are-AudioBook probe was evaluated and declined: it would
    /// cost a query on every real book-folder confirm to close a breadth no
    /// producer exhibits. Residual: a Folder subclass outside the controller refs'
    /// concrete kinds passes until listed here - and such kinds exist TODAY, not in a
    /// future ref (gate-marker tail F3): PlaylistsFolder (Emby.Server.Implementations,
    /// concrete at both pinned tags) plus any plugin-derived Folder. The pass-through
    /// breadth is present, not future; re-derive the list when the envelope moves.
    /// </summary>
    internal static bool IsBookDisambiguationPayload(BaseItem? item)
        => item is AudioBook
           || (item is Folder
               && item is not (MediaBrowser.Controller.Entities.Audio.MusicAlbum
                   or MediaBrowser.Controller.Entities.Audio.MusicArtist
                   or MediaBrowser.Controller.Entities.Audio.MusicGenre
                   or MediaBrowser.Controller.Entities.CollectionFolder
                   or MediaBrowser.Controller.Entities.UserRootFolder
                   or MediaBrowser.Controller.Entities.UserView
                   or MediaBrowser.Controller.Entities.AggregateFolder
                   or MediaBrowser.Controller.Entities.PhotoAlbum
                   or MediaBrowser.Controller.Entities.Movies.BoxSet
                   or MediaBrowser.Controller.Entities.TV.Season
                   or MediaBrowser.Controller.Entities.TV.Series
                   or MediaBrowser.Controller.Playlists.Playlist
                   or MediaBrowser.Controller.Channels.Channel));

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

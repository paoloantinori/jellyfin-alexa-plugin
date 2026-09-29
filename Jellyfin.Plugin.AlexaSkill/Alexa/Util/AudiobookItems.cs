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
        // Item + two ancestors covers the deepest real book shape (book/subfolder/
        // chapter = two ParentId hops); a plain song burns at most its album + folder
        // lookups on the gated path (radio-on or PostPlay=AutoPlay exhaustion only).
        BaseItem? cursor = item;
        for (int hop = 0; cursor != null && hop < 3; hop++)
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

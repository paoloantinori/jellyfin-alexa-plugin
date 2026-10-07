using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE trailing-filename-number chapter comparator (JF-790), extracted from
/// the concat endpoint's private copy (VideoAudioController's
/// <c>_chapterNumberRegex</c> + its OrderBy lambda, added there because
/// "Jellyfin doesn't always parse these into IndexNumber, and SortName/Name may
/// be identical across all chapters"): audiobook chapter FILES carry the true
/// chapter order (001.mp3..100.mp3), a datum no query-layer key can reach
/// (ItemSortBy has no Path axis, JF-672 reflection-dumped both TFMs), so both
/// consumers that need true chapter order sort by it in memory: the endpoint's
/// concat enumeration and the DEFAULT paged AudioPlayer queue path (the untagged
/// class this task fixes). ONE definition is load-bearing here (the JF-382
/// no-second-copy rule): the endpoint and the queue must not drift, because the
/// seek-mode resume math over the concat timeline and the queue's
/// next/previous navigation describe the SAME book.
/// The sort is STABLE (LINQ OrderBy): chapters whose filenames carry no trailing
/// number (or an unparsable one) all key to <see cref="int.MaxValue"/> and keep
/// the query's pinned (<see cref="QueueContinuationFetcher.AudiobookChapterOrder"/>)
/// order among themselves, so a book of numberless files is ordered exactly as
/// the DB served it.
/// ONE deliberate deviation from the endpoint's original lambda, required by the
/// new consumer: the number parses through int.TryParse instead of
/// int.Parse. The endpoint's int.Parse THREW (OverflowException)
/// on a filename whose trailing digit run exceeds int (a timestamp or ISBN
/// suffix, e.g. "9780062315009.mp3"); propagating that crash into the skill's
/// play path would fail the whole request, and the endpoint itself now inherits
/// the tolerance (unparsable numbers sort last, with the numberless files).
/// </summary>
internal static class ChapterFileNameOrder
{
    /// <summary>
    /// Trailing digit run of a filename ("The Upside of Irrationality 065.mp3"
    /// to 65), the shared extraction the endpoint's former private regex owned.
    /// </summary>
    internal static readonly Regex TrailingNumberRegex = new(@"(\d+)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The sort key of one chapter: the trailing number of its filename, or
    /// <see cref="int.MaxValue"/> when the path is empty, the filename carries
    /// no trailing digits, or the digit run does not parse as an int (the
    /// documented tolerance, see the class doc). Equal keys keep source order
    /// (stable sort).
    /// </summary>
    /// <param name="chapter">The chapter row.</param>
    /// <returns>The trailing filename number, or int.MaxValue.</returns>
    internal static int TrailingNumberSortKey(BaseItem chapter)
    {
        string? path = chapter.Path;
        if (string.IsNullOrEmpty(path))
        {
            return int.MaxValue;
        }

        string filename = System.IO.Path.GetFileNameWithoutExtension(path);
        var match = TrailingNumberRegex.Match(filename);
        return match.Success
            && int.TryParse(
                match.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int number)
            ? number
            : int.MaxValue;
    }

    /// <summary>
    /// Sorts chapters by trailing filename number (stable: equal keys keep the
    /// enumeration's order, so the query's pinned order survives among
    /// numberless files). The ONE comparator both the concat endpoint and the
    /// queue path consume; a change here flips both (pinned from both
    /// directions by the endpoint/resolver IL pins and the queue order tests).
    /// </summary>
    /// <param name="chapters">The chapters to sort.</param>
    /// <returns>The sorted list (a new list; the input is untouched).</returns>
    internal static List<BaseItem> SortByTrailingFileNameNumber(IEnumerable<BaseItem> chapters)
        => chapters.OrderBy(TrailingNumberSortKey).ToList();

    /// <summary>
    /// JF-790 detection: whether an initial chapters page proves the DB order
    /// cannot be trusted as chapter order, from page-visible evidence only (the
    /// probe's two shapes, evidence in the JF-672/JF-790 task records):
    /// <list type="number">
    /// <item>The UNTAGGED class: every page row lacks IndexNumber. The DB order
    /// then rests on lexicographic SortName alone, which is provably wrong for
    /// digit-named chapters ("1", "10", "11", ..., "2") even when the first
    /// page itself happens to look numerically consistent (the death shows at
    /// the 14-before-2 boundary BEYOND the page, so no page-local order
    /// comparison can catch it; the class signal is the only sound trigger).
    /// This sweeps in the untagged books that play correctly today via
    /// zero-padded names: for them the filename sort is a no-op (their files
    /// agree with their padded names; numberless files keep DB order through
    /// the stable sort), which is the accepted cost of a class-based trigger.
    /// Rows carrying IndexNumber (the tagged class, 13-of-14 correct books)
    /// never fire this trigger.</item>
    /// <item>The TIE shape: two page rows share (SortName, Name). The pinned
    /// DB order keys on exactly that pair, so a full tie is server whim (the
    /// 100 identical rows of "The Upside of Irrationality"; the within-part
    /// ties of the two-part books). This trigger does not require untagged
    /// rows: tagged books with full ties are equally arbitrary.</item>
    /// </list>
    /// Pages shorter than two rows carry no order evidence (a single row
    /// cannot tie and the class signal is vacuous): not detectable.
    /// </summary>
    /// <param name="pageItems">The initial page's chapter rows.</param>
    /// <returns>True when the queue path must fall back to filename order.</returns>
    internal static bool PageDistrustsDbOrder(IReadOnlyList<BaseItem> pageItems)
    {
        if (pageItems.Count < 2)
        {
            return false;
        }

        bool anyRowTagged = false;
        var seenKeys = new HashSet<(string? SortName, string? Name)>();
        bool fullKeyTie = false;
        foreach (BaseItem row in pageItems)
        {
            if (row.IndexNumber.HasValue)
            {
                anyRowTagged = true;
            }

            if (!seenKeys.Add((row.SortName, row.Name)))
            {
                fullKeyTie = true;
            }
        }

        return !anyRowTagged || fullKeyTie;
    }
}

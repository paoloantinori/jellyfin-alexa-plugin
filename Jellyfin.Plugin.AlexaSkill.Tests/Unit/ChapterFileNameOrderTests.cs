using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-790 unit pins for the ONE trailing-filename-number chapter comparator and
/// its page-distrust detection (ChapterFileNameOrder): the extraction semantics
/// the concat endpoint's former private regex owned, the stable-tie contract
/// that keeps numberless files on the query's pinned order, the TryParse
/// tolerance the shared helper added for the skill's play path, and the
/// two-triggers detection (the untagged class, the full (SortName, Name) tie).
/// The BOTH-DIRECTIONS pin closes the JF-382 no-second-copy rule mechanically:
/// the concat endpoint and the default queue path (AudiobookPlayResolver) must
/// both consume this helper, scanned through the shared IlCallScanner.
/// </summary>
public class ChapterFileNameOrderTests
{
    private static Audio Chapter(Guid id, string? path, string? sortName = null, string? name = null, int? indexNumber = null)
        => new() { Id = id, Name = name, SortName = sortName, IndexNumber = indexNumber, Path = path };

    // --- the comparator ---

    [Fact]
    public void SortByTrailingFileNameNumber_OrdersByTrailingFileNumber()
    {
        // The JF-672 census shape: the DB order (the input) is arbitrary vs the
        // file names; 003 carries no parseable... 003 parses; "extras.mp3" carries
        // no number and keeps source order among the MaxValue keys.
        List<BaseItem> dbOrder = new()
        {
            Chapter(Guid.NewGuid(), "/books/u/012.mp3"),
            Chapter(Guid.NewGuid(), "/books/u/003.mp3"),
            Chapter(Guid.NewGuid(), "/books/u/extras.mp3"),
            Chapter(Guid.NewGuid(), "/books/u/001.mp3"),
            Chapter(Guid.NewGuid(), "/books/u/007.mp3")
        };

        List<BaseItem> sorted = ChapterFileNameOrder.SortByTrailingFileNameNumber(dbOrder);

        Assert.Equal(
            new[] { "001", "003", "007", "012", "extras" },
            sorted.Select(c => System.IO.Path.GetFileNameWithoutExtension(c.Path)).ToArray());
    }

    [Fact]
    public void SortByTrailingFileNameNumber_IsStableForEqualKeys()
    {
        // Two numberless files (and one without a path at all): all MaxValue, so
        // the query's pinned order survives among them (LINQ OrderBy is stable).
        BaseItem a = Chapter(Guid.NewGuid(), "/books/u/disc1-intro.mp3");
        BaseItem b = Chapter(Guid.NewGuid(), null);
        BaseItem c = Chapter(Guid.NewGuid(), "/books/u/disc1-outro.mp3");

        List<BaseItem> sorted = ChapterFileNameOrder.SortByTrailingFileNameNumber(new[] { a, b, c });

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, sorted.Select(x => x.Id));
    }

    [Fact]
    public void TrailingNumberSortKey_ToleratesUnparsableDigitRuns()
    {
        // The deliberate deviation from the endpoint's int.Parse: a digit run
        // beyond int (a timestamp or ISBN suffix) must not crash the play path;
        // it sorts with the numberless files (MaxValue).
        Assert.Equal(int.MaxValue, ChapterFileNameOrder.TrailingNumberSortKey(
            Chapter(Guid.NewGuid(), "/books/u/9780062315009.mp3")));
        Assert.Equal(65, ChapterFileNameOrder.TrailingNumberSortKey(
            Chapter(Guid.NewGuid(), "/books/u/The Upside of Irrationality 065.mp3")));
        // The regex runs on the filename WITHOUT the extension, trailing spaces
        // inside it tolerated.
        Assert.Equal(7, ChapterFileNameOrder.TrailingNumberSortKey(
            Chapter(Guid.NewGuid(), "/books/u/book 7.mp3")));
    }

    [Fact]
    public void SortByTrailingFileNameNumber_InputUntouched()
    {
        List<BaseItem> dbOrder = new()
        {
            Chapter(Guid.NewGuid(), "/books/u/002.mp3"),
            Chapter(Guid.NewGuid(), "/books/u/001.mp3")
        };
        ChapterFileNameOrder.SortByTrailingFileNameNumber(dbOrder);
        Assert.Equal(new[] { "002", "001" }, dbOrder.Select(c => System.IO.Path.GetFileNameWithoutExtension(c.Path)).ToArray());
    }

    // --- the detection ---

    [Fact]
    public void PageDistrustsDbOrder_AllRowsUntagged_IsTrue()
    {
        // The "Thinking Better" class: IndexNumber NULL on every row, so the DB
        // order rests on lexicographic SortName alone. No tie is needed (and the
        // page itself can look numerically consistent; the death shows beyond it).
        List<BaseItem> page = new()
        {
            Chapter(Guid.NewGuid(), "/b/1.mp3", sortName: "1", name: "1", indexNumber: null),
            Chapter(Guid.NewGuid(), "/b/10.mp3", sortName: "10", name: "10", indexNumber: null),
            Chapter(Guid.NewGuid(), "/b/11.mp3", sortName: "11", name: "11", indexNumber: null)
        };

        Assert.True(ChapterFileNameOrder.PageDistrustsDbOrder(page));
    }

    [Fact]
    public void PageDistrustsDbOrder_TaggedDistinctKeys_IsFalse()
    {
        // The tagged class: IndexNumber present and the (SortName, Name) key
        // distinct per row; byte-identical DB paging (the 13-of-14 books).
        List<BaseItem> page = new()
        {
            Chapter(Guid.NewGuid(), "/b/01.mp3", sortName: "0001 - 0001 - Chapter 01", name: "Chapter 01", indexNumber: 1),
            Chapter(Guid.NewGuid(), "/b/02.mp3", sortName: "0001 - 0002 - Chapter 02", name: "Chapter 02", indexNumber: 2)
        };

        Assert.False(ChapterFileNameOrder.PageDistrustsDbOrder(page));
    }

    [Fact]
    public void PageDistrustsDbOrder_FullKeyTie_IsTrue()
    {
        // The "Upside of Irrationality" / "Art of Deception" shape: the full
        // (SortName, Name) key ties, so the pinned order is server whim even
        // with IndexNumber present.
        List<BaseItem> page = new()
        {
            Chapter(Guid.NewGuid(), "/b/05.mp3", sortName: "0001 - Part 1", name: "Part 1", indexNumber: 1),
            Chapter(Guid.NewGuid(), "/b/02.mp3", sortName: "0001 - Part 1", name: "Part 1", indexNumber: 1)
        };

        Assert.True(ChapterFileNameOrder.PageDistrustsDbOrder(page));
    }

    [Fact]
    public void PageDistrustsDbOrder_MixedTaggedAndUntaggedDistinctKeys_IsFalse()
    {
        // The one mixed book of the census (tagged rows plus an untagged stray
        // that sorts last today): page-visible rows carry a tag, so the page
        // stays on the DB path.
        List<BaseItem> page = new()
        {
            Chapter(Guid.NewGuid(), "/b/01.mp3", sortName: "0001 - 0001 - Chapter 01", name: "Chapter 01", indexNumber: 1),
            Chapter(Guid.NewGuid(), "/b/02.mp3", sortName: "0001 - 0002 - Chapter 02", name: "Chapter 02", indexNumber: 2),
            Chapter(Guid.NewGuid(), "/b/stray.mp3", sortName: "Stray", name: "Stray", indexNumber: null)
        };

        Assert.False(ChapterFileNameOrder.PageDistrustsDbOrder(page));
    }

    [Fact]
    public void PageDistrustsDbOrder_SingleRow_IsFalse()
    {
        // One row carries no order evidence (it cannot tie and the class signal
        // is vacuous): not detectable.
        List<BaseItem> page = new()
        {
            Chapter(Guid.NewGuid(), "/b/1.mp3", sortName: "1", name: "1", indexNumber: null)
        };

        Assert.False(ChapterFileNameOrder.PageDistrustsDbOrder(page));
    }

    // --- the both-directions pin (JF-382 no-second-copy, mechanical) ---

    /// <summary>
    /// True when ANY method of the consumer type (its nested-type closure
    /// included, so lambdas and local functions cannot hide a fork) calls the
    /// shared <see cref="ChapterFileNameOrder.SortByTrailingFileNameNumber"/>.
    /// Same-assembly methoddef tokens, the WarmingGateCoverageTests technique.
    /// </summary>
    private static bool ConsumesSharedComparator(Type consumerType)
    {
        int[] helperTokens = IlCallScanner.MethodTokens(
            typeof(ChapterFileNameOrder),
            nameof(ChapterFileNameOrder.SortByTrailingFileNameNumber)).ToArray();

        foreach (MethodBase method in IlCallScanner.NestedTypeClosure(consumerType)
                     .SelectMany(IlCallScanner.DeclaredCallableMethods))
        {
            if (IlCallScanner.ContainsCallToAnyToken(method, helperTokens))
            {
                return true;
            }
        }

        return false;
    }

    // A change to the comparator must flip BOTH consumers together: if either
    // side forks a private copy (a second regex, a local OrderBy lambda), this
    // reds and the JF-382 rule is restored before the drift reaches a book.
    [Fact]
    public void ConcatEndpoint_ConsumesTheSharedComparator()
    {
        Assert.True(ConsumesSharedComparator(typeof(
            global::Jellyfin.Plugin.AlexaSkill.Controller.VideoAudioController)));
    }

    [Fact]
    public void QueuePath_ConsumesTheSharedComparator()
    {
        Assert.True(ConsumesSharedComparator(typeof(AudiobookPlayResolver)));
    }

    // --- the cached-continuation tail (the audiobook arm's JF-790 shape) ---

    private static QueueContinuation CachedBookContinuation(
        List<BaseItem> sortedChapters,
        int startIndex,
        int batchSize)
        => new()
        {
            SourceType = "Audiobook",
            ParentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = startIndex,
            TotalCount = sortedChapters.Count,
            BatchSize = batchSize,
            CachedTracks = sortedChapters
        };

    [Fact]
    public void FetchNextBatch_AudiobookCachedTracks_ServesTheSortedListAndNeverQueries()
    {
        List<BaseItem> sorted = Enumerable.Range(1, 14)
            .Select(n => Chapter(Guid.NewGuid(), $"/b/{n:00}.mp3", name: n.ToString()))
            .ToList<BaseItem>();
        var continuation = CachedBookContinuation(sorted, startIndex: 5, batchSize: 10);

        // The tail must serve memory only: any query here would serve the DB
        // order that answered wrong for this class.
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new InvalidOperationException("the cached continuation must not query the DB"));

        IReadOnlyList<BaseItem> batch = QueueContinuationFetcher.FetchNextBatch(
            continuation, library.Object, new Mock<IUserManager>().Object,
            new Mock<ILogger>().Object);

        Assert.Equal(sorted.Skip(5).Select(c => c.Id), batch.Select(c => c.Id));
        Assert.Equal(14, continuation.StartIndex);
    }

    [Fact]
    public void FetchNextBatch_AudiobookCachedTracks_ExhaustsAtTheTotal()
    {
        List<BaseItem> sorted = Enumerable.Range(1, 9)
            .Select(n => Chapter(Guid.NewGuid(), $"/b/{n:00}.mp3", name: n.ToString()))
            .ToList<BaseItem>();
        var continuation = CachedBookContinuation(sorted, startIndex: 5, batchSize: 10);

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new InvalidOperationException("the cached continuation must not query the DB"));

        IReadOnlyList<BaseItem> batch = QueueContinuationFetcher.FetchNextBatch(
            continuation, library.Object, new Mock<IUserManager>().Object,
            new Mock<ILogger>().Object);

        Assert.Equal(4, batch.Count);
        Assert.Equal(9, continuation.StartIndex);
        // The entry guard: the next call is terminal, no query, no zero-page WARN.
        Assert.Empty(QueueContinuationFetcher.FetchNextBatch(
            continuation, library.Object, new Mock<IUserManager>().Object,
            new Mock<ILogger>().Object));
    }
}

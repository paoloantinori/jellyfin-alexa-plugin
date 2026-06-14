using System;
using System.IO;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for AudiobookPositionTracker: high-water-mark Math.Max, conservative (−1 segment)
/// read, zero-when-empty, and Clear. Pure unit test — no Plugin.Instance.
/// </summary>
public class AudiobookPositionTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AudiobookPositionTracker _tracker;

    private const long TicksPerSegment = 10 * TimeSpan.TicksPerSecond; // 10s segments

    public AudiobookPositionTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "abpos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tracker = new AudiobookPositionTracker(_tempDir, LoggerFactory.Create(b => { }).CreateLogger<AudiobookPositionTracker>());
    }

    public void Dispose()
    {
        _tracker.Dispose();
        try { if (Directory.Exists(_tempDir)) { Directory.Delete(_tempDir, true); } } catch { }
    }

    [Fact]
    public void GetPositionTicks_ReturnsZero_WhenNoData()
    {
        Assert.Equal(0, _tracker.GetPositionTicks("book1"));
    }

    [Fact]
    public void GetPositionTicks_ReturnsZero_WhenEmptyId()
    {
        _tracker.RecordSegment("book1", 5);
        Assert.Equal(0, _tracker.GetPositionTicks(""));
    }

    [Fact]
    public void RecordSegment_KeepsHighWaterMark_OnLowerSegment()
    {
        _tracker.RecordSegment("book1", 5);
        _tracker.RecordSegment("book1", 2); // went back (seek) — must not lower the mark

        // Conservative read: (5 - 1) * 10s
        Assert.Equal(4 * TicksPerSegment, _tracker.GetPositionTicks("book1"));
    }

    [Fact]
    public void GetPositionTicks_IsConservative_OffByOneSegment()
    {
        _tracker.RecordSegment("book1", 1);
        Assert.Equal(0, _tracker.GetPositionTicks("book1")); // (1-1)*10s = 0

        _tracker.RecordSegment("book1", 3);
        Assert.Equal(2 * TicksPerSegment, _tracker.GetPositionTicks("book1")); // (3-1)*10s
    }

    [Fact]
    public void RecordSegment_TracksAcrossBooksIndependently()
    {
        _tracker.RecordSegment("bookA", 10);
        _tracker.RecordSegment("bookB", 3);

        Assert.Equal(9 * TicksPerSegment, _tracker.GetPositionTicks("bookA"));
        Assert.Equal(2 * TicksPerSegment, _tracker.GetPositionTicks("bookB"));
    }

    [Fact]
    public void Clear_RemovesPosition()
    {
        _tracker.RecordSegment("book1", 5);
        Assert.True(_tracker.GetPositionTicks("book1") > 0);

        _tracker.Clear("book1");
        Assert.Equal(0, _tracker.GetPositionTicks("book1"));
    }

    [Fact]
    public void RecordSegment_IgnoresNegativeAndEmpty()
    {
        _tracker.RecordSegment("", 5);
        _tracker.RecordSegment("book1", -1);
        Assert.Equal(0, _tracker.GetPositionTicks("book1"));
    }
}

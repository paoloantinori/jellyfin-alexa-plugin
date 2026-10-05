using System;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for AudiobookPlaylistBuilder: #EXT-X-START injection, position after VERSION line,
/// segment preservation, default strategy, and non-positive start no-op.
/// </summary>
public class AudiobookPlaylistBuilderTests
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private const string BasePlaylist = @"#EXTM3U
#EXT-X-VERSION:3
#EXT-X-TARGETDURATION:10
#EXT-X-MEDIA-SEQUENCE:0
#EXTINF:10.0,
seg_0000.ts
#EXTINF:10.0,
seg_0001.ts
#EXTINF:10.0,
seg_0002.ts
";

    [Fact]
    public void ActiveStrategy_IsSliced() // Echo ignores #EXT-X-START, so Sliced is active
    {
        Assert.Equal(AudiobookPlaylistBuilder.ResumeStrategy.Sliced, AudiobookPlaylistBuilder.ActiveStrategy);
    }

    [Fact]
    public void BuildResumePlaylist_ReturnsBase_WhenStartNonPositive()
    {
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, 0, 10);
        Assert.Equal(BasePlaylist, result);
    }

    [Fact]
    public void BuildResumePlaylist_Sliced_DropsSegmentsBeforeStart()
    {
        // startTicks = 20s → startSegment 2. Sliced playlist keeps seg_0002+, drops seg_0000/0001.
        long startTicks = 20 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, startTicks, 10);

        Assert.DoesNotContain("seg_0000.ts", result);
        Assert.DoesNotContain("seg_0001.ts", result);
        Assert.Contains("seg_0002.ts", result);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:2", result);
    }

    [Fact]
    public void BuildResumePlaylist_Sliced_PreservesHeaderAndSegmentCount()
    {
        long startTicks = 10 * TicksPerSecond; // startSegment 1 → keeps seg_0001, seg_0002
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, startTicks, 10);

        Assert.Contains("#EXTM3U", result);
        Assert.Contains("#EXT-X-VERSION:3", result);
        Assert.Contains("#EXT-X-TARGETDURATION:10", result);
        // 3 segments in base, dropping seg_0000 → 2 remain
        Assert.Equal(2, result.Split("seg_", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BuildResumePlaylist_Sliced_ZeroStartReturnsBase()
    {
        string result = AudiobookPlaylistBuilder.BuildSlicedPlaylist(BasePlaylist, 0, 10);
        Assert.Equal(BasePlaylist, result);
    }

    /// <summary>
    /// JF-686 review F3: when the base playlist carries NO #EXT-X-MEDIA-SEQUENCE, the
    /// synthesized tag must land immediately AFTER #EXTM3U. RFC 8216 requires EXTM3U to
    /// be the first line, so the old leading insert (index 0) emitted an invalid
    /// playlist. RED (the insert reverted to index 0): the output starts with the
    /// sequence tag and this assert flips.
    /// </summary>
    [Fact]
    public void BuildResumePlaylist_Sliced_NoSequenceTag_InsertsAfterExtm3u()
    {
        const string noSequence = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n"
            + "#EXTINF:10.0,\nseg_0000.ts\n#EXTINF:10.0,\nseg_0001.ts\n#EXTINF:10.0,\nseg_0002.ts\n";

        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(noSequence, 10 * TicksPerSecond, 10);

        Assert.StartsWith("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:1\n", result, StringComparison.Ordinal);
        Assert.Contains("seg_0001.ts", result, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0000.ts", result, StringComparison.Ordinal);
    }

    // ========== JF-499 W2: caller-supplied segment duration (episode remux is 4s) ==========

    private const string FourSecondPlaylist = @"#EXTM3U
#EXT-X-VERSION:3
#EXT-X-TARGETDURATION:4
#EXT-X-MEDIA-SEQUENCE:0
#EXTINF:4.0,
seg_0000.ts
#EXTINF:4.0,
seg_0001.ts
#EXTINF:4.0,
seg_0002.ts
#EXTINF:4.0,
seg_0003.ts
#EXTINF:4.0,
seg_0004.ts
";

    /// <summary>
    /// JF-499 W2: a 4-second-segment playlist (the episode remux shape) slices at
    /// startTicks/4s, not startTicks/10s: 16s resumes at seg_0004 under the episode
    /// duration (the audiobook 10s nominal would wrongly slice at seg_0001).
    /// </summary>
    [Fact]
    public void BuildResumePlaylist_Sliced_WithEpisodeSegmentDuration_SlicesAtFourSecondBoundary()
    {
        long startTicks = 16 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(FourSecondPlaylist, startTicks, segmentDurationSeconds: 4);

        Assert.DoesNotContain("seg_0000.ts", result);
        Assert.DoesNotContain("seg_0003.ts", result);
        Assert.Contains("seg_0004.ts", result);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:4", result);
    }

    /// <summary>
    /// JF-499 P2 fix: when the playlist carries NO #EXTINF durations at all, the
    /// slice falls back to the flat divisor over the caller's nominal
    /// segmentDurationSeconds (16s: floor(16/10) = segment 1, floor(16/4) = segment 4).
    /// The parameter is now REQUIRED, so this pins the fallback arithmetic the
    /// audiobook path (nominal 10s) and the episode remux (nominal 4s) pass in.
    /// </summary>
    [Fact]
    public void BuildResumePlaylist_Sliced_NoExtinf_FallsBackToFlatDivisor()
    {
        const string noExtinf = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n"
            + "seg_0000.ts\nseg_0001.ts\nseg_0002.ts\nseg_0003.ts\nseg_0004.ts\n";
        long startTicks = 16 * TicksPerSecond;

        string byTen = AudiobookPlaylistBuilder.BuildResumePlaylist(noExtinf, startTicks, 10);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:1", byTen);

        string byFour = AudiobookPlaylistBuilder.BuildResumePlaylist(noExtinf, startTicks, 4);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:4", byFour);
    }

    /// <summary>
    /// JF-499 P2 fix: a duration that FAILS TO PARSE also routes to the flat-divisor
    /// fallback (the whole walk gives up, not just the one segment).
    /// </summary>
    [Fact]
    public void BuildResumePlaylist_Sliced_UnparseableExtinf_FallsBackToFlatDivisor()
    {
        const string garbage = "#EXTM3U\n#EXTINF:not-a-number,\nseg_0000.ts\n#EXTINF:not-a-number,\nseg_0001.ts\n";
        long startTicks = 16 * TicksPerSecond;

        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(garbage, startTicks, 4);

        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:4", result);
    }

    // ========== JF-499 P2 fix: EXTINF accumulation (remux segments land on the source GOP) ==========

    /// <summary>
    /// JF-499 P2 fix: the episode remux is -c:v copy with -hls_time 4, so real
    /// segments land on the source GOP (4-10s) and the completed ffmpeg playlist
    /// carries the ACTUAL per-segment #EXTINF durations. The slice must ACCUMULATE
    /// those durations, not divide by the nominal 4s: for uniform 6.000s segments a
    /// 60s resume starts at seg_0010 (60/6); the flat 4s divisor would land at
    /// seg_0015, 1.5x too deep.
    /// </summary>
    [Fact]
    public void BuildResumePlaylist_Sliced_SixSecondRealSegments_DeepResumeNotTooDeep()
    {
        var playlist = new System.Text.StringBuilder()
            .Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < 20; i++)
        {
            playlist.Append("#EXTINF:6.000,\n").Append("seg_").Append(i.ToString("D4")).Append(".ts\n");
        }

        long startTicks = 60 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(playlist.ToString(), startTicks, 4);

        Assert.DoesNotContain("seg_0009.ts", result);
        Assert.Contains("seg_0010.ts", result);
        // The flat-4s divisor would start at seg_0015 (dropping 0010-0014); the
        // accumulated real durations keep 0010-0014 and start at 0010.
        Assert.Contains("seg_0014.ts", result);
        Assert.Contains("seg_0019.ts", result);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:10", result);
    }

    /// <summary>
    /// JF-499 P2 fix: with genuinely NON-uniform durations the accumulated start
    /// offsets decide the boundary: durations 4,6,6,6,... put 16s at the start of
    /// seg_0003 (4+6+6), where the flat 4s divisor would say seg_0004.
    /// </summary>
    [Fact]
    public void BuildResumePlaylist_Sliced_NonUniformExtinf_AccumulatesStartOffsets()
    {
        const string mixed = @"#EXTM3U
#EXT-X-VERSION:3
#EXT-X-TARGETDURATION:6
#EXT-X-MEDIA-SEQUENCE:0
#EXTINF:4.000,
seg_0000.ts
#EXTINF:6.000,
seg_0001.ts
#EXTINF:6.000,
seg_0002.ts
#EXTINF:6.000,
seg_0003.ts
#EXTINF:6.000,
seg_0004.ts
";
        long startTicks = 16 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(mixed, startTicks, 4);

        Assert.DoesNotContain("seg_0002.ts", result);
        Assert.Contains("seg_0003.ts", result);
        Assert.Contains("seg_0004.ts", result);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:3", result);
    }

    // The StartHint strategy is currently dormant (Sliced is active because the Echo ignores
    // #EXT-X-START), but its code path is exercised directly here in case it's re-enabled.

    [Fact]
    public void BuildStartHintPlaylist_InsertsStartAfterVersionLine()
    {
        long startTicks = 45 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildStartHintPlaylist(BasePlaylist, startTicks);

        int versionIdx = result.IndexOf("#EXT-X-VERSION", StringComparison.Ordinal);
        int startIdx = result.IndexOf("#EXT-X-START", StringComparison.Ordinal);
        int targetIdx = result.IndexOf("#EXT-X-TARGETDURATION", StringComparison.Ordinal);

        Assert.True(versionIdx >= 0 && startIdx > versionIdx, "START must come after VERSION");
        Assert.True(targetIdx > startIdx, "START must come before TARGETDURATION");
    }

    [Fact]
    public void BuildStartHintPlaylist_PreservesAllSegments()
    {
        long startTicks = 60 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildStartHintPlaylist(BasePlaylist, startTicks);

        int baseSegs = BasePlaylist.Split("seg_", StringSplitOptions.None).Length - 1;
        int resultSegs = result.Split("seg_", StringSplitOptions.None).Length - 1;
        Assert.Equal(baseSegs, resultSegs);
    }

    [Fact]
    public void BuildStartHintPlaylist_FractionalOffset_FormattedToThreeDecimals()
    {
        long startTicks = (long)(12.3456 * TicksPerSecond);
        string result = AudiobookPlaylistBuilder.BuildStartHintPlaylist(BasePlaylist, startTicks);

        Assert.Contains("#EXT-X-START:TIME-OFFSET=12.346,PRECISE=YES", result);
    }

    [Fact]
    public void BuildStartHintPlaylist_HandlesMissingVersionLine()
    {
        string noVersion = "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10.0,\nseg_0000.ts\n";
        long startTicks = 30 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildStartHintPlaylist(noVersion, startTicks);

        Assert.Contains("#EXT-X-START:TIME-OFFSET=30.000,PRECISE=YES", result);
    }

    // ---------- JF-778: TruncateToFirstSegments (the windowed prewrite serve's emitter) ----------

    /// <summary>
    /// JF-778: truncation keeps the header plus the first N (EXTINF, URI) pairs,
    /// drops the tail AND any trailing ENDLIST (the windowed listing must stay an
    /// event playlist: the player keeps polling for growth), and never leaks the
    /// first DROPPED segment's EXTINF as a phantom entry (the pending-flush
    /// boundary; an EXTINF without its URI skews every duration walk downstream).
    /// </summary>
    [Fact]
    public void TruncateToFirstSegments_KeepsPrefixDropsTailAndEndlist()
    {
        string playlist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n"
            + "#EXTINF:4.0,\nseg_0000.ts?token=t\n#EXTINF:4.0,\nseg_0001.ts?token=t\n#EXTINF:4.0,\nseg_0002.ts?token=t\n"
            + "#EXT-X-ENDLIST\n";

        string truncated = AudiobookPlaylistBuilder.TruncateToFirstSegments(playlist, maxSegments: 2);

        Assert.StartsWith("#EXTM3U", truncated, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", truncated, StringComparison.Ordinal);
        Assert.Contains("seg_0000.ts?token=t", truncated, StringComparison.Ordinal);
        Assert.Contains("seg_0001.ts?token=t", truncated, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0002", truncated, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", truncated, StringComparison.Ordinal);
        // Exactly two EXTINF lines: the boundary EXTINF of the dropped segment
        // must not survive as a third.
        Assert.Equal(2, truncated.Split("#EXTINF").Length - 1);
    }

    /// <summary>
    /// JF-778: a maxSegments at or above the listing's own count keeps every line
    /// (ENDLIST included; no truncation engages), and CRLF input survives the
    /// walk with CR stripped on the kept lines.
    /// </summary>
    [Fact]
    public void TruncateToFirstSegments_AtOrAboveCount_UnchangedAndCrlfTolerant()
    {
        string playlist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.0,\nseg_0000.ts\n#EXTINF:4.0,\nseg_0001.ts\n#EXT-X-ENDLIST\n";

        string unchanged = AudiobookPlaylistBuilder.TruncateToFirstSegments(playlist, maxSegments: 5);
        Assert.Equal(playlist, unchanged);

        string crlf = playlist.Replace("\n", "\r\n");
        string truncated = AudiobookPlaylistBuilder.TruncateToFirstSegments(crlf, maxSegments: 1);
        Assert.Contains("seg_0000.ts", truncated, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0001", truncated, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", truncated, StringComparison.Ordinal);
    }
}

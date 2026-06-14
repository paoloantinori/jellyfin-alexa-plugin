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
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, 0);
        Assert.Equal(BasePlaylist, result);
    }

    [Fact]
    public void BuildResumePlaylist_Sliced_DropsSegmentsBeforeStart()
    {
        // startTicks = 20s → startSegment 2. Sliced playlist keeps seg_0002+, drops seg_0000/0001.
        long startTicks = 20 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, startTicks);

        Assert.DoesNotContain("seg_0000.ts", result);
        Assert.DoesNotContain("seg_0001.ts", result);
        Assert.Contains("seg_0002.ts", result);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:2", result);
    }

    [Fact]
    public void BuildResumePlaylist_Sliced_PreservesHeaderAndSegmentCount()
    {
        long startTicks = 10 * TicksPerSecond; // startSegment 1 → keeps seg_0001, seg_0002
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, startTicks);

        Assert.Contains("#EXTM3U", result);
        Assert.Contains("#EXT-X-VERSION:3", result);
        Assert.Contains("#EXT-X-TARGETDURATION:10", result);
        // 3 segments in base, dropping seg_0000 → 2 remain
        Assert.Equal(2, result.Split("seg_", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BuildResumePlaylist_Sliced_ZeroStartReturnsBase()
    {
        string result = AudiobookPlaylistBuilder.BuildSlicedPlaylist(BasePlaylist, 0);
        Assert.Equal(BasePlaylist, result);
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
}

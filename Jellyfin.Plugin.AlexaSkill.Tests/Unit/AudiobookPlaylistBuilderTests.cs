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
    public void ActiveStrategy_DefaultsToStartHint()
    {
        Assert.Equal(AudiobookPlaylistBuilder.ResumeStrategy.StartHint, AudiobookPlaylistBuilder.ActiveStrategy);
    }

    [Fact]
    public void BuildResumePlaylist_ReturnsBase_WhenStartNonPositive()
    {
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, 0);
        Assert.Equal(BasePlaylist, result);
    }

    [Fact]
    public void BuildResumePlaylist_ContainsExtXStart_WhenStartPositive()
    {
        long startTicks = 120 * TicksPerSecond; // 2 minutes
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, startTicks);

        Assert.Contains("#EXT-X-START:TIME-OFFSET=120.000,PRECISE=YES", result);
    }

    [Fact]
    public void BuildResumePlaylist_InsertsStartAfterVersionLine()
    {
        long startTicks = 45 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, startTicks);

        int versionIdx = result.IndexOf("#EXT-X-VERSION", StringComparison.Ordinal);
        int startIdx = result.IndexOf("#EXT-X-START", StringComparison.Ordinal);
        int targetIdx = result.IndexOf("#EXT-X-TARGETDURATION", StringComparison.Ordinal);

        Assert.True(versionIdx >= 0 && startIdx > versionIdx, "START must come after VERSION");
        Assert.True(targetIdx > startIdx, "START must come before TARGETDURATION");
    }

    [Fact]
    public void BuildResumePlaylist_PreservesAllSegments()
    {
        long startTicks = 60 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, startTicks);

        int baseSegs = BasePlaylist.Split("seg_", StringSplitOptions.None).Length - 1;
        int resultSegs = result.Split("seg_", StringSplitOptions.None).Length - 1;
        Assert.Equal(baseSegs, resultSegs);
    }

    [Fact]
    public void BuildResumePlaylist_FractionalOffset_FormattedToThreeDecimals()
    {
        long startTicks = (long)(12.3456 * TicksPerSecond);
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(BasePlaylist, startTicks);

        Assert.Contains("#EXT-X-START:TIME-OFFSET=12.346,PRECISE=YES", result);
    }

    [Fact]
    public void BuildResumePlaylist_HandlesMissingVersionLine()
    {
        string noVersion = "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10.0,\nseg_0000.ts\n";
        long startTicks = 30 * TicksPerSecond;
        string result = AudiobookPlaylistBuilder.BuildResumePlaylist(noVersion, startTicks);

        Assert.Contains("#EXT-X-START:TIME-OFFSET=30.000,PRECISE=YES", result);
    }
}

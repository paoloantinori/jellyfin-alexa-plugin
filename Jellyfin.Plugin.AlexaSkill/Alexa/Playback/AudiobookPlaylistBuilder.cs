using System;
using System.Text;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Builds resume-aware HLS playlists from a base audiobook concat playlist.
/// The active strategy is a single const so flipping between StartHint and Sliced is trivial.
/// </summary>
public static class AudiobookPlaylistBuilder
{
    private const int SegmentDurationSeconds = 10;

    /// <summary>
    /// Which resume strategy to use.
    /// <list type="bullet">
    /// <item><c>StartHint</c> (default): emit the full playlist with an
    /// <c>#EXT-X-START:TIME-OFFSET</c> hint — full seek bar, resumes at position IF the
    /// player honors the tag. Risk: an ignoring player resumes from 0.</item>
    /// <item><c>Sliced</c>: emit a playlist beginning at the target segment — guaranteed to
    /// resume at position, but the seek bar only covers [resumePoint → end].</item>
    /// </list>
    /// </summary>
    public enum ResumeStrategy
    {
        StartHint,
        Sliced
    }

    /// <summary>
    /// The resume strategy currently in use. Flip to <see cref="ResumeStrategy.Sliced"/> if
    /// hardware testing shows the Echo ignores <c>#EXT-X-START</c>.
    /// </summary>
    public const ResumeStrategy ActiveStrategy = ResumeStrategy.StartHint;

    /// <summary>
    /// Build a resume playlist from a base playlist and a start position (ticks).
    /// Delegates to the active strategy. A non-positive startTicks returns the playlist as-is.
    /// </summary>
    /// <param name="basePlaylist">The full audiobook HLS playlist.</param>
    /// <param name="startTicks">Resume position in .NET ticks (100ns units).</param>
    /// <returns>A playlist configured to resume at the given position.</returns>
    public static string BuildResumePlaylist(string basePlaylist, long startTicks)
    {
        if (startTicks <= 0)
        {
            return basePlaylist;
        }

        return ActiveStrategy switch
        {
            ResumeStrategy.StartHint => BuildStartHintPlaylist(basePlaylist, startTicks),
            ResumeStrategy.Sliced => BuildSlicedPlaylist(basePlaylist, startTicks),
            _ => BuildStartHintPlaylist(basePlaylist, startTicks)
        };
    }

    /// <summary>
    /// Emit the full playlist with an <c>#EXT-X-START:TIME-OFFSET=&lt;secs&gt;,PRECISE=YES</c>
    /// tag inserted after <c>#EXT-X-VERSION</c>. Keeps the full seek bar; the player starts
    /// at the offset. RISK: ExoPlayer (Echo Show) may ignore <c>#EXT-X-START</c>.
    /// </summary>
    internal static string BuildStartHintPlaylist(string basePlaylist, long startTicks)
    {
        double seconds = startTicks / (double)TimeSpan.TicksPerSecond;
        string startTag = $"#EXT-X-START:TIME-OFFSET={seconds:F3},PRECISE=YES";

        string[] lines = basePlaylist.Split('\n');
        var output = new StringBuilder(basePlaylist.Length + startTag.Length + 2);
        bool inserted = false;
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');
            output.AppendLine(line);
            if (!inserted && line.StartsWith("#EXT-X-VERSION", StringComparison.Ordinal))
            {
                output.AppendLine(startTag);
                inserted = true;
            }
        }

        if (!inserted)
        {
            // No VERSION line found — prepend after #EXTM3U as a safe fallback.
            return $"#EXTM3U\n{startTag}\n" + basePlaylist;
        }

        return output.ToString();
    }

    /// <summary>
    /// Fallback: emit a playlist beginning at segment N = floor(startTicks / 10s). Loses the
    /// ability to seek backward before N, but is guaranteed to resume at N on any HLS player.
    /// Complete this only if hardware testing shows <c>#EXT-X-START</c> is ignored.
    /// </summary>
    internal static string BuildSlicedPlaylist(string basePlaylist, long startTicks)
    {
        int startSegment = (int)(startTicks / (TimeSpan.TicksPerSecond * SegmentDurationSeconds));
        if (startSegment <= 0)
        {
            return basePlaylist;
        }

        // TODO: filter playlist to segments >= startSegment, adjust #EXT-X-MEDIA-SEQUENCE,
        // drop earlier #EXTINF/URI pairs. Until implemented, delegate to StartHint so the
        // feature degrades to the hint behavior rather than failing.
        return BuildStartHintPlaylist(basePlaylist, startTicks);
    }
}

using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Builds resume-aware HLS playlists from a base audiobook concat playlist.
/// The active strategy is a single const so flipping between StartHint and Sliced is trivial.
/// </summary>
public static class AudiobookPlaylistBuilder
{
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
    /// The resume strategy currently in use.
    /// <para><b>Sliced</b> is active: the Echo Show's ExoPlayer ignores <c>#EXT-X-START</c>
    /// (verified on hardware — resume restarted from 0 even with the hint correctly served),
    /// so we slice the playlist to begin at the target segment instead. This keeps the seek bar
    /// over [resumePoint → end] and reliably resumes at position.</para>
    /// </summary>
    public const ResumeStrategy ActiveStrategy = ResumeStrategy.Sliced;

    /// <summary>
    /// Build a resume playlist from a base playlist and a start position (ticks).
    /// Delegates to the active strategy. A non-positive startTicks returns the playlist as-is.
    /// </summary>
    /// <param name="basePlaylist">The full audiobook HLS playlist.</param>
    /// <param name="startTicks">Resume position in .NET ticks (100ns units).</param>
    /// <param name="segmentDurationSeconds">The nominal segment length the slice
    /// arithmetic divides by when the playlist carries no parseable
    /// <c>#EXTINF</c> durations (the flat-divisor FALLBACK; the audiobook path
    /// passes its 10s const, the episode remux its 4s, JF-499 W2). Ignored by the
    /// StartHint strategy.</param>
    /// <returns>A playlist configured to resume at the given position.</returns>
    public static string BuildResumePlaylist(string basePlaylist, long startTicks, int segmentDurationSeconds)
    {
        if (startTicks <= 0)
        {
            return basePlaylist;
        }

        return ActiveStrategy switch
        {
            ResumeStrategy.StartHint => BuildStartHintPlaylist(basePlaylist, startTicks),
            ResumeStrategy.Sliced => BuildSlicedPlaylist(basePlaylist, startTicks, segmentDurationSeconds),
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
    /// Emit a playlist beginning at the resume segment N, resolved by ACCUMULATING
    /// the playlist's own per-segment <c>#EXTINF</c> durations until the cumulative
    /// start offset reaches <paramref name="startTicks"/> (JF-499 P2 fix: the
    /// episode remux is <c>-c:v copy</c> with <c>-hls_time 4</c>, so real segments
    /// land on the source GOP, 4-10s, and the completed ffmpeg playlist carries the
    /// ACTUAL per-segment durations; a flat division by the nominal duration lands
    /// up to ~1.5x too deep). The flat divisor over
    /// <paramref name="segmentDurationSeconds"/> is kept as the FALLBACK for
    /// playlists whose durations fail to parse or that carry none.
    /// Keeps segments N..end, sets <c>#EXT-X-MEDIA-SEQUENCE:N</c>, and preserves the
    /// header + ENDLIST. Loses the ability to seek backward before N, but is guaranteed
    /// to resume at N on any HLS player (uses the same event-playlist mechanism
    /// first-play relies on). This is the active strategy because the Echo Show
    /// ignores <c>#EXT-X-START</c>.
    /// </summary>
    internal static string BuildSlicedPlaylist(string basePlaylist, long startTicks, int segmentDurationSeconds)
    {
        int startSegment = TryResolveStartSegmentByExtinf(basePlaylist, startTicks)
            ?? (int)(startTicks / (TimeSpan.TicksPerSecond * segmentDurationSeconds));
        if (startSegment <= 0)
        {
            return basePlaylist;
        }

        string[] lines = basePlaylist.Split('\n');
        var output = new StringBuilder(basePlaylist.Length);
        string? pendingInf = null; // buffered #EXTINF awaiting its URI line
        bool mediaSequenceSet = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            // A segment URI line (non-tag, references seg_NNNN.ts). Pair it with the buffered EXTINF.
            if (!line.StartsWith('#') && line.Length > 0 && line.Contains("seg_", StringComparison.Ordinal))
            {
                int segNum = TryParseSegmentNumber(line);
                if (segNum >= startSegment && pendingInf != null)
                {
                    output.AppendLine(pendingInf);
                    output.AppendLine(line);
                }

                pendingInf = null;
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
            {
                pendingInf = line;
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.Ordinal))
            {
                output.AppendLine("#EXT-X-MEDIA-SEQUENCE:" + startSegment.ToString(CultureInfo.InvariantCulture));
                mediaSequenceSet = true;
                continue;
            }

            // Other header/closing tags (EXTM3U, VERSION, TARGETDURATION, ENDLIST, …) pass through.
            output.AppendLine(line);
        }

        // A sliced playlist MUST declare the first segment's sequence; add it if the base lacked one.
        if (!mediaSequenceSet)
        {
            output.Insert(0, "#EXT-X-MEDIA-SEQUENCE:" + startSegment.ToString(CultureInfo.InvariantCulture) + "\n");
        }

        return output.ToString();
    }

    /// <summary>
    /// Resolve the resume segment by accumulating the playlist's own per-segment
    /// <c>#EXTINF</c> durations: the first segment whose cumulative start offset
    /// reaches <paramref name="startTicks"/> (JF-499 P2). Returns null when the
    /// playlist carries no parseable durations, or the position lies beyond its
    /// total, so the caller falls back to the flat divisor.
    /// Uses the same segment-URI line predicate as the emitter so indices align.
    /// </summary>
    /// <param name="basePlaylist">The full playlist text.</param>
    /// <param name="startTicks">Resume position in .NET ticks.</param>
    /// <returns>The resume segment index, or null when accumulation is impossible.</returns>
    private static int? TryResolveStartSegmentByExtinf(string basePlaylist, long startTicks)
    {
        string[] lines = basePlaylist.Split('\n');
        long cumulativeTicks = 0;
        int segmentIndex = 0;
        double? pendingSeconds = null; // buffered #EXTINF awaiting its segment URI line

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            if (!line.StartsWith('#') && line.Length > 0 && line.Contains("seg_", StringComparison.Ordinal))
            {
                if (pendingSeconds is not double seconds)
                {
                    // A segment without a parseable #EXTINF: the walk cannot be exact.
                    return null;
                }

                if (cumulativeTicks >= startTicks)
                {
                    return segmentIndex;
                }

                cumulativeTicks += (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
                segmentIndex++;
                pendingSeconds = null;
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
            {
                pendingSeconds = TryParseExtInfSeconds(line);
            }
        }

        // startTicks lies beyond the playlist's total duration (stale resume value):
        // fall back to the flat divisor rather than slicing everything away.
        return null;
    }

    /// <summary>
    /// Parse the duration seconds out of an <c>#EXTINF:&lt;duration&gt;[,&lt;title&gt;]</c>
    /// line, invariant-culture. Returns null on any parse failure.
    /// </summary>
    private static double? TryParseExtInfSeconds(string line)
    {
        int colon = line.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        int end = line.IndexOf(',', colon + 1);
        if (end < 0)
        {
            end = line.Length;
        }

        return double.TryParse(
            line.AsSpan(colon + 1, end - colon - 1),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double seconds)
            ? seconds
            : null;
    }

    /// <summary>
    /// Parse the 3–4 digit segment number from a <c>seg_NNN[N].ts</c> URI line. Returns -1 on failure.
    /// </summary>
    private static int TryParseSegmentNumber(string line)
    {
        int idx = line.IndexOf("seg_", StringComparison.Ordinal);
        if (idx < 0)
        {
            return -1;
        }

        int start = idx + 4;
        int end = start;
        while (end < line.Length && end < start + 4 && line[end] >= '0' && line[end] <= '9')
        {
            end++;
        }

        return int.TryParse(line.AsSpan(start, end - start), out int n) ? n : -1;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MediaBrowser.Model.Lyrics;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// JF-926: the ONE lyric-lines to ASS subtitle converter behind the burned-in lyrics
/// view. Input is the server's own <see cref="LyricDto.Lyrics"/> list (served by
/// ILyricManager for the song's .lrc sidecar); output is a v4.00+ ASS script whose
/// dialogue events carry one line per lyric, timed from each line's <c>Start</c>
/// ticks (the 12.x DTO field; lines without a Start are UNSYNCED text and are
/// SKIPPED per line, with a whole-input refusal only when fewer than two synced
/// lines remain, the v1 no-synced-lyrics contract).
/// The style block is the JF-925 spike's device-verified shape (white, sans-serif,
/// bottom-center, black background at 1280x720): proven readable on the Echo Show
/// with the production jellyfin-ffmpeg libass burn-in (spike 2026-11-09, the
/// organic-lrc frame check). Deterministic and pure: same lines in, same bytes
/// out (the lyrics cache key depends on it).
/// </summary>
public static class LyricsAssBuilder
{
    /// <summary>
    /// The spike-verified ASS header + style, byte-identical to the render that
    /// produced the verified frames (crf28 1fps black 1280x720). Keep in sync with
    /// the spike report claudedocs/spike_jf-925_lyrics_2026-11-09.md.
    /// </summary>
    private const string HeaderAndStyle =
        "[Script Info]\nScriptType: v4.00+\nPlayResX: 1280\nPlayResY: 720\n\n"
        + "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
        + "Style: Lyric,Arial,54,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,60,1\n\n"
        + "[Events]\nFormat: Layer, Start, End, Style, Text\n";

    /// <summary>
    /// Build the ASS script from the song's lyric lines. Returns false (and a null
    /// script) when there is no SYNCED lyric content: fewer than two lines, or no
    /// line carrying a <c>Start</c> tick (the plain-text unsynced shape). A single
    /// synced line is technically renderable but carries no scrolling value; the
    /// honest v1 contract treats it as unsynced.
    /// </summary>
    /// <param name="lines">The lyric lines served by ILyricManager.</param>
    /// <param name="script">The ASS script, or null on refusal.</param>
    /// <returns>True when a synced script was built.</returns>
    public static bool TryBuild(IReadOnlyList<LyricLine> lines, out string? script)
    {
        script = null;
        if (lines == null)
        {
            return false;
        }

        List<(long StartTicks, string Text)> synced = lines
            .Where(l => l?.Start is not null && !string.IsNullOrWhiteSpace(l.Text))
            // Negative starts (LRC offset shifts) clamp to the song start: truncating
            // division would otherwise put the sign into the h/m/s fields and mint a
            // malformed timestamp.
            .Select(l => (StartTicks: Math.Max(0, l.Start!.Value), Text: l.Text.Trim()))
            .OrderBy(t => t.StartTicks)
            .ToList();
        if (synced.Count < 2)
        {
            return false;
        }

        // Exact-ish capacity: the ~64-char dialogue overhead per line plus the
        // escaped text itself (the fixed prefix alone is ~41 chars).
        var sb = new StringBuilder(
            HeaderAndStyle.Length + (synced.Count * 64) + synced.Sum(t => t.Text.Length));
        sb.Append(HeaderAndStyle);
        for (int i = 0; i < synced.Count; i++)
        {
            long start = synced[i].StartTicks;
            // The line's window runs to the next line's start; the last line gets a
            // five-second tail (the song usually ends sooner). Duplicate Start
            // values give the earlier line a zero-length window: deterministic
            // (OrderBy is stable), renderable, later duplicate wins the visible
            // render; accepted.
            long end = i + 1 < synced.Count
                ? synced[i + 1].StartTicks
                : start + (TimeSpan.TicksPerSecond * 5);
            sb.Append("Dialogue: 0,")
                .Append(AssTimestamp(start))
                .Append(',')
                .Append(AssTimestamp(end))
                .Append(",Lyric,")
                .Append(Escape(synced[i].Text))
                .Append('\n');
        }

        script = sb.ToString();
        return true;
    }

    /// <summary>
    /// Ticks (10M per second) to the ASS timestamp <c>h:mm:ss.cc</c>. Culture-
    /// invariant by construction (integer formatting only).
    /// </summary>
    /// <param name="ticks">The line start in .NET ticks.</param>
    /// <returns>The ASS timestamp.</returns>
    internal static string AssTimestamp(long ticks)
    {
        long centiseconds = ticks / 100_000;
        // One hour is 3,600 seconds = 360,000 centiseconds.
        long hours = centiseconds / 360_000;
        long rem = centiseconds % 360_000;
        long minutes = rem / 6_000;
        rem %= 6_000;
        long seconds = rem / 100L;
        long cs = rem % 100;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours}:{minutes:D2}:{seconds:D2}.{cs:D2}");
    }

    /// <summary>
    /// ASS dialogue text escaping: line breaks become spaces (one event per line;
    /// a lone carriage return too, so old-Mac endings do not merge adjacent words)
    /// and braces are the ASS override-tag delimiters, so they are balanced-disabled
    /// with a backslash (a stray brace pair would swallow visible text as a tag).
    /// Commas need no escaping: the Events Format declares Text as the LAST field,
    /// so a conforming parser takes the entire line remainder as the text.
    /// </summary>
    /// <param name="text">The lyric line text.</param>
    /// <returns>The escaped dialogue text.</returns>
    private static string Escape(string text)
        => text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal);
}

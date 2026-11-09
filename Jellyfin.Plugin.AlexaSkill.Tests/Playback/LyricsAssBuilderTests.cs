using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using MediaBrowser.Model.Lyrics;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Playback;

/// <summary>
/// JF-926 pins for the lyrics ASS builder: the synced gate (the v1 contract), the
/// tick-to-timestamp math, the next-line windowing with the last-line tail, and
/// the ASS override-tag escaping. The style block is pinned byte-identical to the
/// JF-925 spike's device-verified render.
/// </summary>
public class LyricsAssBuilderTests
{
    private static LyricLine Line(string text, long? start = null) => new(text, start);

    [Fact]
    public void TryBuild_SyncedLines_BuildsScriptWithWindows()
    {
        bool ok = LyricsAssBuilder.TryBuild(
            new List<LyricLine>
            {
                Line("first", 0),
                Line("second", 8_000_0000),
                Line("third", 16_000_0000),
            },
            out string? script);

        Assert.True(ok);
        Assert.NotNull(script);
        Assert.Equal(3, script!.Split('\n').Count(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)));
        Assert.Contains("Dialogue: 0,0:00:00.00,0:00:08.00,Lyric,first", script, StringComparison.Ordinal);
        Assert.Contains("Dialogue: 0,0:00:08.00,0:00:16.00,Lyric,second", script, StringComparison.Ordinal);
        // The last line gets the five-second tail (the spike shape).
        Assert.Contains("Dialogue: 0,0:00:16.00,0:00:21.00,Lyric,third", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_UnsyncedTextOnly_Refuses()
    {
        bool ok = LyricsAssBuilder.TryBuild(
            new List<LyricLine> { Line("a"), Line("b"), Line("c") },
            out string? script);

        Assert.False(ok);
        Assert.Null(script);
    }

    [Fact]
    public void TryBuild_NullOrEmptyOrSingleLine_Refuses()
    {
        Assert.False(LyricsAssBuilder.TryBuild(null!, out _));
        Assert.False(LyricsAssBuilder.TryBuild(new List<LyricLine>(), out _));
        Assert.False(LyricsAssBuilder.TryBuild(new List<LyricLine> { Line("only", 0) }, out _));
    }

    [Fact]
    public void TryBuild_LinesWithMixedSync_SkipsUnsyncedLines()
    {
        bool ok = LyricsAssBuilder.TryBuild(
            new List<LyricLine> { Line("header note"), Line("real", 50_000_000), Line("also real", 90_000_000) },
            out string? script);

        Assert.True(ok);
        string[] dialogues = script!.Split('\n').Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, dialogues.Length);
        Assert.DoesNotContain("header note", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_EscapesAssOverrideDelimitersAndKeepsCommas()
    {
        bool ok = LyricsAssBuilder.TryBuild(
            new List<LyricLine> { Line("{\\pos(1,1)}weird", 0), Line("two", 9_000_0000) },
            out string? script);

        Assert.True(ok);
        // The FULL line pin (quality review JF-926 finding 1): brace pair, comma
        // literalness (Text is the LAST Events field, commas need no escaping), and
        // the window timestamp in one assert; a builder escaping only one brace
        // shape can no longer pass.
        Assert.Contains(
            "Dialogue: 0,0:00:00.00,0:00:09.00,Lyric,\\{\\pos(1,1)\\}weird",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_NegativeStartClampsToSongStart()
    {
        bool ok = LyricsAssBuilder.TryBuild(
            new List<LyricLine> { Line("shifted", -3_723_100_0000L), Line("b", 9_000_0000) },
            out string? script);

        Assert.True(ok);
        // The negative LRC-offset shift floors at 0: truncating division would
        // otherwise mint a malformed "-1:-02:-03.-10" timestamp.
        Assert.Contains("Dialogue: 0,0:00:00.00,0:00:09.00,Lyric,shifted", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-1:-", script, StringComparison.Ordinal);
    }

    // The newline-flattening half of Escape (code-review JF-926 round-2 finding 2):
    // it was the ONLY escaping the device-verified spike exercised, yet no pin.
    // A raw break inside the Events section would land the lyric tail on a
    // non-Dialogue line that libass silently skips.
    [Fact]
    public void TryBuild_EmbeddedLineBreaks_FlattenToSpaces()
    {
        bool ok = LyricsAssBuilder.TryBuild(
            new List<LyricLine> { Line("two\r\nword line", 0), Line("b", 9_000_0000) },
            out string? script);

        Assert.True(ok);
        Assert.Contains("Dialogue: 0,0:00:00.00,0:00:09.00,Lyric,two  word line", script, StringComparison.Ordinal);
    }

    // libass backslash specials (code-review JF-926 round-2 finding 1): outside
    // override blocks the parser turns \N into a hard break, \n into a soft break,
    // \h into a non-breaking space. Karaoke-LRC lines carry literal \N splits;
    // all three flatten to spaces like any other break (the one-event-per-line
    // policy); any other backslash renders literally and stays untouched.
    [Fact]
    public void TryBuild_LibassBackslashSpecials_FlattenToSpaces()
    {
        bool ok = LyricsAssBuilder.TryBuild(
            new List<LyricLine> { Line("Back\\Nto back\\na\\hb\\keep", 0), Line("b", 9_000_0000) },
            out string? script);

        Assert.True(ok);
        Assert.Contains("Dialogue: 0,0:00:00.00,0:00:09.00,Lyric,Back to back a b\\keep", script, StringComparison.Ordinal);
    }

    // The duplicate-Start tie (code-review JF-926 round-2 finding 4): both lines
    // share a tick (the negative-clamp shape clamps two offset lines to 0); the
    // EARLIER line gets a zero-length window so the LATER duplicate wins the
    // visible render. OrderBy is stable, so the behavior is deterministic; this
    // pins it so a future de-dup or re-window cannot change it silently.
    [Fact]
    public void TryBuild_DuplicateStart_EarlierLineGetsZeroWindow_LaterWins()
    {
        bool ok = LyricsAssBuilder.TryBuild(
            new List<LyricLine> { Line("first", 0), Line("second", 0), Line("third", 9_000_0000) },
            out string? script);

        Assert.True(ok);
        Assert.Contains("Dialogue: 0,0:00:00.00,0:00:00.00,Lyric,first", script, StringComparison.Ordinal);
        Assert.Contains("Dialogue: 0,0:00:00.00,0:00:09.00,Lyric,second", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_HourRolloverTimestamp_RendersInvariantly()
    {
        bool ok = LyricsAssBuilder.TryBuild(
            new List<LyricLine> { Line("a", 0), Line("b", 3_723_100_0000L) }, // 1:02:03.10
            out string? script);

        Assert.True(ok);
        Assert.Contains("1:02:03.10,", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_SpikeStyleBlock_IsByteIdentical()
    {
        LyricsAssBuilder.TryBuild(
            new List<LyricLine> { Line("a", 0), Line("b", 9_000_0000) },
            out string? script);

        // The JF-925 spike's device-verified style: a drift here changes the render
        // for every locale without any test noticing, so pin it whole.
        Assert.StartsWith(
            "[Script Info]\nScriptType: v4.00+\nPlayResX: 1280\nPlayResY: 720\n\n"
            + "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
            + "Style: Lyric,Arial,54,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,60,1\n\n"
            + "[Events]\nFormat: Layer, Start, End, Style, Text\n",
            script,
            StringComparison.Ordinal);
    }
}

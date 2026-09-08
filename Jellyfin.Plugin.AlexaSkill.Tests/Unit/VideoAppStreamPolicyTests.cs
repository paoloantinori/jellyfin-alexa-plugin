using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for <see cref="VideoAppStreamPolicy"/> (JF-498/JF-500): the codec probe
/// that decides static-vs-HLS (remux or video transcode tier) for every
/// Movie/Episode VideoApp launch. The Echo Show decodes H.264 video only and has
/// no EAC3/AC3/TrueHD/DTS audio decoder, so the static byte stream never starts
/// for such sources.
/// </summary>
public class VideoAppStreamPolicyTests
{
    // ========== The task matrix: mkv+eac3 -> HLS, mp4+aac -> static, mkv+aac -> documented decision ==========

    /// <summary>
    /// The evidenced library shape (corr=d9f848a7): MKV, H.264 High video, EAC3
    /// audio. The static URL serves raw bytes ExoPlayer cannot decode, so the launch
    /// must route through the HLS remux (video copy + AAC audio).
    /// </summary>
    [Fact]
    public void Decide_MkvH264Eac3_RoutesToHlsRemux()
    {
        VideoAppStreamDecision decision = VideoAppStreamPolicy.Decide("h264", "eac3", "mkv");

        Assert.Equal(VideoAppStreamRoute.HlsRemux, decision.Route);
        Assert.Contains("eac3", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>mp4 + h264 + aac is the fully compatible shape: static stream.</summary>
    [Fact]
    public void Decide_Mp4H264Aac_KeepsStaticStream()
    {
        VideoAppStreamDecision decision = VideoAppStreamPolicy.Decide("h264", "aac", "mp4");

        Assert.Equal(VideoAppStreamRoute.Static, decision.Route);
    }

    /// <summary>
    /// The DOCUMENTED mkv+aac decision: static. The container deliberately does not
    /// trigger the remux: ExoPlayer extracts Matroska natively and the evidenced
    /// failure is codec-level (no EAC3 decoder), so an h264+aac MKV keeps working
    /// behavior instead of paying a remux. This test pins that decision.
    /// </summary>
    [Fact]
    public void Decide_MkvH264Aac_KeepsStaticStream()
    {
        VideoAppStreamDecision decision = VideoAppStreamPolicy.Decide("h264", "aac", "mkv");

        Assert.Equal(VideoAppStreamRoute.Static, decision.Route);
    }

    /// <summary>
    /// Container independence: swapping the container never changes the decision
    /// (the whole matrix holds for both mp4 and mkv sources).
    /// </summary>
    [Theory]
    [InlineData("mkv")]
    [InlineData("mp4")]
    [InlineData("webm")]
    [InlineData(null)]
    public void Decide_ContainerDoesNotChangeTheRoute(string? container)
    {
        Assert.Equal(VideoAppStreamRoute.HlsRemux, VideoAppStreamPolicy.Decide("h264", "eac3", container).Route);
        Assert.Equal(VideoAppStreamRoute.Static, VideoAppStreamPolicy.Decide("h264", "aac", container).Route);
        Assert.Equal(VideoAppStreamRoute.HlsTranscode, VideoAppStreamPolicy.Decide("hevc", "aac", container).Route);
    }

    // ========== Incompatible audio codecs ==========

    [Theory]
    [InlineData("eac3")]
    [InlineData("ac3")]
    [InlineData("truehd")]
    [InlineData("dts")]
    [InlineData("dtshd")]
    [InlineData("dts-hd")]
    public void Decide_H264WithIncompatibleAudio_RoutesToHlsRemux(string audioCodec)
    {
        VideoAppStreamDecision decision = VideoAppStreamPolicy.Decide("h264", audioCodec);

        Assert.Equal(VideoAppStreamRoute.HlsRemux, decision.Route);
    }

    /// <summary>
    /// Codec names are compared case-insensitively (ffprobe lowercases, but the
    /// probe must not depend on it).
    /// </summary>
    [Fact]
    public void Decide_CaseInsensitiveCodecs_RoutesToHlsRemux()
    {
        Assert.Equal(VideoAppStreamRoute.HlsRemux, VideoAppStreamPolicy.Decide("H264", "EAC3").Route);
    }

    /// <summary>
    /// Audio codecs NOT on the incompatible list keep the static stream: the list is
    /// deliberately conservative (a false positive costs a remux on every play).
    /// </summary>
    [Theory]
    [InlineData("aac")]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("opus")]
    public void Decide_H264WithCompatibleAudio_KeepsStaticStream(string audioCodec)
    {
        Assert.Equal(VideoAppStreamRoute.Static, VideoAppStreamPolicy.Decide("h264", audioCodec).Route);
    }

    /// <summary>Unknown audio codec: keep the static stream (may only add the remux, never change working behavior).</summary>
    [Fact]
    public void Decide_H264UnknownAudio_KeepsStaticStream()
    {
        VideoAppStreamDecision decision = VideoAppStreamPolicy.Decide("h264", null);

        Assert.Equal(VideoAppStreamRoute.Static, decision.Route);
    }

    // ========== Non-h264 video: the JF-500 transcode tier ==========

    /// <summary>
    /// JF-500: a KNOWN non-h264 video codec routes to the episode HLS video
    /// TRANSCODE tier (H.264 re-encode) instead of the first cut's warned static
    /// fallback: these sources now play. AV1 rides the same tier. The reason
    /// names the codec so triage knows what re-encodes.
    /// </summary>
    [Theory]
    [InlineData("hevc")]
    [InlineData("av1")]
    [InlineData("mpeg2video")]
    [InlineData("vp9")]
    public void Decide_NonH264Video_RoutesToHlsTranscode(string videoCodec)
    {
        VideoAppStreamDecision decision = VideoAppStreamPolicy.Decide(videoCodec, "eac3");

        Assert.Equal(VideoAppStreamRoute.HlsTranscode, decision.Route);
        Assert.Contains(videoCodec, decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The transcode tier is picked for a non-h264 source REGARDLESS of the audio
    /// codec: even a hevc+aac source (Echo-decodable audio) cannot play statically
    /// because the video track is undecodable.
    /// </summary>
    [Fact]
    public void Decide_HevcWithCompatibleAudio_StillRoutesToHlsTranscode()
    {
        VideoAppStreamDecision decision = VideoAppStreamPolicy.Decide("hevc", "aac");

        Assert.Equal(VideoAppStreamRoute.HlsTranscode, decision.Route);
    }

    /// <summary>
    /// The HANDLER-side tier predicate (used by Decide): true for every KNOWN
    /// non-h264 codec, false for h264 and for an unknown codec (fail-open, may
    /// only ADD the route).
    /// </summary>
    [Theory]
    [InlineData("hevc", true)]
    [InlineData("av1", true)]
    [InlineData("HEVC", true)]
    [InlineData("h264", false)]
    [InlineData("H264", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    public void VideoRequiresTranscode_KnownNonH264Only(string? videoCodec, bool expected)
    {
        Assert.Equal(expected, VideoAppStreamPolicy.VideoRequiresTranscode(videoCodec));
    }

    /// <summary>
    /// JF-500 review R1: the CONTROLLER-side tier predicate, the deliberate inverse
    /// of <see cref="VideoRequiresTranscode"/>: true ONLY for a known h264 codec,
    /// so an unknown codec maps to the transcode tier at the endpoint instead of a
    /// copy remux of unverifiable bytes (a completed remux is cached forever).
    /// </summary>
    [Theory]
    [InlineData("h264", true)]
    [InlineData("H264", true)]
    [InlineData("hevc", false)]
    [InlineData("av1", false)]
    [InlineData("mjpeg", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    public void VideoSupportsRemux_KnownH264Only(string? videoCodec, bool expected)
    {
        Assert.Equal(expected, VideoAppStreamPolicy.VideoSupportsRemux(videoCodec));
    }

    /// <summary>
    /// An UNKNOWN video codec still keeps the static stream: the transcode cannot
    /// be guaranteed to start when the source video codec was never read.
    /// </summary>
    [Fact]
    public void Decide_UnknownVideoCodec_KeepsStaticStreamEvenWithIncompatibleAudio()
    {
        VideoAppStreamDecision decision = VideoAppStreamPolicy.Decide(null, "eac3");

        Assert.Equal(VideoAppStreamRoute.Static, decision.Route);
    }

    // ========== Codec extraction from media streams ==========

    /// <summary>
    /// ExtractCodecs picks the FIRST video and FIRST audio stream and lowercases
    /// them, skipping subtitle streams (MKV sources carry PGS/SRT that must not
    /// influence the decision).
    /// </summary>
    [Fact]
    public void ExtractCodecs_PicksFirstVideoAndAudioStreams()
    {
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "H264" },
            new() { Type = MediaStreamType.Audio, Codec = "EAC3" },
            new() { Type = MediaStreamType.Subtitle, Codec = "subrip" },
            new() { Type = MediaStreamType.Audio, Codec = "aac" }
        };

        (string? video, string? audio) = VideoAppStreamPolicy.ExtractCodecs(streams);

        Assert.Equal("h264", video);
        Assert.Equal("eac3", audio);
    }

    /// <summary>Audio-only items (no video stream) yield a null video codec, which Decide maps to Static.</summary>
    [Fact]
    public void ExtractCodecs_AudioOnlyItem_HasNoVideoCodec()
    {
        var streams = new List<MediaStream> { new() { Type = MediaStreamType.Audio, Codec = "flac" } };

        (string? video, string? audio) = VideoAppStreamPolicy.ExtractCodecs(streams);

        Assert.Null(video);
        Assert.Equal("flac", audio);
    }

    [Fact]
    public void ExtractCodecs_NullStreams_YieldsNullCodecs()
    {
        (string? video, string? audio) = VideoAppStreamPolicy.ExtractCodecs(null);

        Assert.Null(video);
        Assert.Null(audio);
    }

    // ========== JF-507: the audio-only launch gate ==========

    /// <summary>
    /// JF-507: the audio-only launch (AudioPlayer resume of a video item on a screenless
    /// device) needs the AAC transcode for exactly the Echo-undecodable audio codecs; the
    /// video codec is NOT consulted (an audio-only stream has no video track).
    /// </summary>
    [Theory]
    [InlineData("eac3")]
    [InlineData("ac3")]
    [InlineData("truehd")]
    [InlineData("dts")]
    [InlineData("dtshd")]
    [InlineData("dts-hd")]
    [InlineData("EAC3")]
    public void AudioRequiresTranscode_EchoUndecodableCodecs_True(string audioCodec)
    {
        Assert.True(VideoAppStreamPolicy.AudioRequiresTranscode(audioCodec));
    }

    /// <summary>
    /// JF-507: decodable audio keeps the raw static URL, and an UNKNOWN codec keeps it
    /// too (the probe may only ADD the transcode route, never break the launch path).
    /// </summary>
    [Theory]
    [InlineData("aac")]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData(null)]
    [InlineData("")]
    public void AudioRequiresTranscode_DecodableOrUnknownCodecs_False(string? audioCodec)
    {
        Assert.False(VideoAppStreamPolicy.AudioRequiresTranscode(audioCodec));
    }
}

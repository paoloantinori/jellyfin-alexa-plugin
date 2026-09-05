using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Static policy that decides static-vs-HLS-remux for a VideoApp launch, from the
/// item's media stream codecs (JF-498: MKV/H.264/EAC3 episodes never started; the
/// Echo Show's ExoPlayer has no EAC3 decoder, so the audio renderer cannot initialize
/// and the static byte stream never begins playing).
/// </summary>
/// <remarks>
/// Policy, from the on-device evidence (corr=d9f848a7):
/// <list type="bullet">
/// <item>Audio codecs eac3/ac3/truehd/dts (incl. dts-hd spellings) have no Echo
/// decoder: they trigger the HLS remux (video copy + AAC audio).</item>
/// <item>H.264 is the only video codec the Echo advertises for third-party VideoApp
/// playback. A non-H.264 video codec (hevc, av1, ...) cannot be fixed by a REMUX:
/// this first cut deliberately has no video transcode path, so such items keep the
/// static URL (today's behavior, no regression) and the decision carries a warning
/// naming the codec.</item>
/// <item>The remux additionally requires the video codec to be KNOWN h264: with an
/// unknown video codec the remux output might be an undecodable stream, so the
/// decision falls back to static.</item>
/// </list>
/// Container note: the container (mkv/mp4/...) deliberately does NOT trigger the
/// remux. ExoPlayer extracts Matroska natively and the evidenced failure is
/// codec-level (no EAC3 decoder), not container-level: an h264+aac MKV plays via
/// the static URL today, so routing it through ffmpeg would change working behavior
/// for no gained compatibility.
/// </remarks>
public static class VideoAppStreamPolicy
{
    /// <summary>
    /// Source audio codecs with no decoder on the Echo Show for third-party VideoApp
    /// playback. Keep conservative: a codec on this list forces an ffmpeg remux of
    /// every play of the item (cache-miss first play costs encode time).
    /// </summary>
    public static readonly HashSet<string> EchoIncompatibleAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "eac3",
        "ac3",
        "truehd",
        "dts",
        "dtshd",
        "dts-hd"
    };

    /// <summary>
    /// The only video codec the Echo Show advertises for third-party VideoApp playback
    /// (H_264_42/41), and the only one the remux path can stream-copy.
    /// </summary>
    public const string EchoCompatibleVideoCodec = "h264";

    /// <summary>
    /// Decide static-vs-HLS-remux for a VideoApp launch. Inputs are the item's first
    /// video/audio stream codecs (lowercase ffprobe names; the comparison is
    /// case-insensitive anyway) and, for documentation purposes only, the container.
    /// </summary>
    /// <param name="videoCodec">First video stream codec, or null when unknown.</param>
    /// <param name="audioCodec">First audio stream codec, or null when unknown.</param>
    /// <param name="container">Source container (mkv, mp4, ...). Deliberately NOT
    /// consulted; see the class remarks for the reasoning. Present in the signature so
    /// the policy surface matches the probe description and future container rules
    /// have a place to land.</param>
    /// <returns>The launch decision with a log-ready reason.</returns>
    public static VideoAppStreamDecision Decide(string? videoCodec, string? audioCodec, string? container = null)
    {
        if (string.IsNullOrWhiteSpace(videoCodec))
        {
            return new VideoAppStreamDecision(
                VideoAppStreamRoute.Static,
                $"video codec unknown; keeping the static stream (cannot guarantee an Echo-decodable remux), audio={audioCodec ?? "unknown"}");
        }

        if (!string.Equals(videoCodec, EchoCompatibleVideoCodec, StringComparison.OrdinalIgnoreCase))
        {
            return new VideoAppStreamDecision(
                VideoAppStreamRoute.Static,
                $"video codec '{videoCodec}' is not h264; the Echo only decodes H.264 and this build has no video transcode path, so the static stream is kept (playback will fail on-device like today)",
                LogWarning: true);
        }

        if (!string.IsNullOrWhiteSpace(audioCodec) && EchoIncompatibleAudioCodecs.Contains(audioCodec))
        {
            return new VideoAppStreamDecision(
                VideoAppStreamRoute.HlsRemux,
                $"audio codec '{audioCodec}' has no Echo decoder; routing through the HLS remux (h264 video copy + AAC audio), container={container ?? "unknown"}");
        }

        return new VideoAppStreamDecision(
            VideoAppStreamRoute.Static,
            $"h264 video + '{audioCodec ?? "unknown"}' audio is Echo-compatible; keeping the static stream, container={container ?? "unknown"}");
    }

    /// <summary>
    /// Extract the first video and first audio codec from an item's media streams.
    /// Used by every caller that holds a <see cref="MediaStream"/> list (the handler
    /// side via <c>BaseItem.GetMediaStreams()</c>, the controller side via
    /// <c>IMediaSourceManager.GetMediaStreams</c>).
    /// </summary>
    /// <param name="streams">The item's media streams, or null.</param>
    /// <returns>The (video, audio) codec names, null when no stream of that type exists.</returns>
    public static (string? VideoCodec, string? AudioCodec) ExtractCodecs(IEnumerable<MediaStream>? streams)
    {
        string? video = null;
        string? audio = null;
        if (streams != null)
        {
            foreach (MediaStream stream in streams)
            {
                if (string.IsNullOrWhiteSpace(stream.Codec))
                {
                    continue;
                }

                if (stream.Type == MediaStreamType.Video && video == null)
                {
                    video = stream.Codec.ToLowerInvariant();
                }
                else if (stream.Type == MediaStreamType.Audio && audio == null)
                {
                    audio = stream.Codec.ToLowerInvariant();
                }

                if (video != null && audio != null)
                {
                    break;
                }
            }
        }

        return (video, audio);
    }
}

/// <summary>
/// Which VideoApp source to launch for a video item (JF-498).
/// </summary>
public enum VideoAppStreamRoute
{
    /// <summary>
    /// Today's behavior: Jellyfin's raw <c>/Videos/{id}/stream?static=true</c> URL
    /// (original container and codecs, byte-for-byte).
    /// </summary>
    Static,

    /// <summary>
    /// The episode HLS remux endpoint (video stream copy + audio AAC transcode into
    /// MPEG-TS segments): used when the source audio codec has no decoder on the
    /// Echo Show, so the static URL plays nothing.
    /// </summary>
    HlsRemux
}

/// <summary>
/// The launch decision plus the reason for it (the reason string is what handlers
/// log, so it names the codecs that drove the decision).
/// </summary>
/// <param name="Route">The source route to use.</param>
/// <param name="Reason">Human-readable reason for the decision (codec names included).</param>
/// <param name="LogWarning">
/// True when the decision should be logged at WARNING level: the item cannot play on
/// the Echo with either route (non-H.264 video), and the user should see it in the logs.
/// </param>
public sealed record VideoAppStreamDecision(VideoAppStreamRoute Route, string Reason, bool LogWarning = false);

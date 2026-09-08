using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Static policy that decides static-vs-HLS for a VideoApp launch (remux or video
/// transcode tier), from the item's media stream codecs (JF-498: MKV/H.264/EAC3
/// episodes never started; the Echo Show's ExoPlayer has no EAC3 decoder, so the
/// audio renderer cannot initialize and the static byte stream never begins playing.
/// JF-500: HEVC/AV1 video sources route to the transcode tier instead of the
/// warned static fallback of the first cut).
/// </summary>
/// <remarks>
/// Policy, from the on-device evidence (corr=d9f848a7):
/// <list type="bullet">
/// <item>Audio codecs eac3/ac3/truehd/dts (incl. dts-hd spellings) have no Echo
/// decoder: they trigger the HLS remux (video copy + AAC audio).</item>
/// <item>H.264 is the only video codec the Echo advertises for third-party VideoApp
/// playback. A non-H.264 video codec (hevc, av1, ...) cannot be fixed by a REMUX:
/// JF-500 routes such items through the episode HLS video TRANSCODE tier (H.264
/// re-encode instead of copy) when the codec is KNOWN; the previously spoken
/// warning-shaped static fallback is gone because these sources now play.</item>
/// <item>The remux additionally requires the video codec to be KNOWN h264: with an
/// unknown video codec the transcode output cannot be guaranteed to start, so the
/// decision falls back to static (the fail-open shape the probe has always had).</item>
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

        if (VideoRequiresTranscode(videoCodec))
        {
            return new VideoAppStreamDecision(
                VideoAppStreamRoute.HlsTranscode,
                $"video codec '{videoCodec}' has no Echo decoder; routing through the episode HLS video transcode (libx264 ultrafast, measured 4.40x realtime, JF-500), audio={audioCodec ?? "unknown"}");
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
    /// Whether a KNOWN video codec needs the H.264 transcode tier of the episode HLS
    /// path (JF-500): anything that is not h264 (hevc, av1, mpeg2video, ...) must be
    /// re-encoded, because the Echo decodes H.264 only and a remux copies the
    /// undecodable bytes. Null/empty (unknown codec) is FALSE: the probe may only
    /// ADD the transcode route, never break the launch path (the endpoint re-probes
    /// server-side anyway). Used by <see cref="Decide"/>; the controller-side tier
    /// pick uses the inverted <see cref="VideoSupportsRemux"/> instead (see its doc
    /// for why the two fail directions deliberately differ).
    /// </summary>
    /// <param name="videoCodec">First video stream codec, or null when unknown.</param>
    /// <returns>True when the source video must be re-encoded to H.264.</returns>
    public static bool VideoRequiresTranscode(string? videoCodec)
        => !string.IsNullOrWhiteSpace(videoCodec)
            && !string.Equals(videoCodec, EchoCompatibleVideoCodec, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a video codec is KNOWN h264, the only shape the episode HLS endpoint
    /// may stream-copy (JF-500 review R1). This is the CONTROLLER-side tier
    /// predicate and deliberately inverts the fail direction of
    /// <see cref="VideoRequiresTranscode"/>: null/empty/unknown returns FALSE, so the
    /// endpoint takes the TRANSCODE tier instead of copying bytes it cannot verify
    /// are h264. A copy remux of undecodable video COMPLETES with ENDLIST and is
    /// then served forever by the endpoint's cache-hit path, which never re-probes;
    /// the handler-side <see cref="Decide"/> keeps its own unknown-to-static
    /// fail-open because it may only ADD routes, never break the launch path.
    /// </summary>
    /// <param name="videoCodec">First video stream codec, or null when unknown.</param>
    /// <returns>True only when the codec is known to be h264 (remux-safe).</returns>
    public static bool VideoSupportsRemux(string? videoCodec)
        => string.Equals(videoCodec, EchoCompatibleVideoCodec, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an AUDIO-ONLY launch of an item (JF-507: the AudioPlayer resume of a
    /// video item on a screenless device) needs the AAC transcode instead of the raw
    /// static <c>/Audio/{id}/stream?static=true</c> URL: the source audio codec has no
    /// decoder on the Echo, so the raw bytes die at ~1ms (live incident 2026-09-06
    /// corr=f0240020: an EAC3 episode audio-resumed on a Dot, MEDIA_ERROR_SERVICE_UNAVAILABLE
    /// at offsetInMilliseconds=1). The video codec is deliberately NOT consulted: an
    /// audio-only stream carries no video track, so a non-H.264 video codec (which forces
    /// Static on the VideoApp path because that path copies video) is no obstacle here.
    /// An unknown codec (null) keeps the static URL: the probe may only ADD the
    /// transcode route, never break the launch path (same fail-open shape as
    /// <see cref="Decide"/>, and the endpoint re-probes server-side anyway).
    /// </summary>
    /// <param name="audioCodec">First audio stream codec, or null when unknown.</param>
    /// <returns>True when the audio-only launch must route to the AAC transcode.</returns>
    public static bool AudioRequiresTranscode(string? audioCodec)
        => !string.IsNullOrWhiteSpace(audioCodec) && EchoIncompatibleAudioCodecs.Contains(audioCodec);

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
    HlsRemux,

    /// <summary>
    /// The episode HLS video TRANSCODE tier (JF-500), same endpoint as
    /// <see cref="HlsRemux"/>: the endpoint re-probes server-side and re-encodes the
    /// video to H.264 (libx264 ultrafast) instead of copying it, for sources whose
    /// VIDEO codec the Echo cannot decode (hevc, av1, ...).
    /// </summary>
    HlsTranscode
}

/// <summary>
/// The launch decision plus the reason for it (the reason string is what handlers
/// log, so it names the codecs that drove the decision).
/// </summary>
/// <param name="Route">The source route to use.</param>
/// <param name="Reason">Human-readable reason for the decision (codec names included).</param>
public sealed record VideoAppStreamDecision(VideoAppStreamRoute Route, string Reason);

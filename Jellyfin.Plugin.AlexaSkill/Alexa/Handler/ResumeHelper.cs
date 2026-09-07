using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Helper for resume-on-relaunch state stored in Alexa session attributes.
/// Uses a proper DTO class (not ValueTuple) for safe JSON serialization.
/// </summary>
internal static class ResumeHelper
{
    /// <summary>
    /// Serializable resume state stored in session attributes.
    /// </summary>
    internal const string ResumeStateKey = "resume_state";

    internal class ResumeState
    {
        [JsonProperty("itemId")]
        public string ItemId { get; set; } = string.Empty;

        [JsonProperty("offsetMs")]
        public long OffsetMs { get; set; }

        /// <summary>
        /// When true, resume via the audiobook HLS resume playlist (VideoApp + #EXT-X-START)
        /// instead of AudioPlayer + offset. Set by the resume-offer builder for audiobooks
        /// with a tracked position under NativeControlsForBooks. Defaults false for backward
        /// compatibility (existing session attributes deserialize without it).
        /// </summary>
        [JsonProperty("useResumePlaylist")]
        public bool UseResumePlaylist { get; set; }

        /// <summary>
        /// True when <see cref="OffsetMs"/> is DEVICE-DERIVED (seeded from the AudioPlayer
        /// context offset) and therefore relative to the previous playback's OUTPUT
        /// timeline: for a transcode-routed item that timeline starts at the stream's
        /// <c>?start=</c> base, so the Yes-side resume must rebase it (recorded base +
        /// offset) before minting the next <c>?start=</c>, or drop it when no base is
        /// recorded (JF-514). False (default, back-compatible: sessions offered before
        /// the deploy deserialize without it) treats the offset as item-absolute. NOTE:
        /// that is an approximation for the server-progress seeds - the device event
        /// writers persist stream-relative offsets for transcode-routed items - so a
        /// flag=false offer can still mint a wrong ?start (known residual, JF-520).
        /// </summary>
        [JsonProperty("offsetIsStreamRelative")]
        public bool OffsetIsStreamRelative { get; set; }
    }

    /// <summary>
    /// Check if session attributes contain an active resume confirmation state.
    /// </summary>
    /// <param name="sessionAttributes">The session attributes dictionary.</param>
    /// <returns>True if resume state is present.</returns>
    public static bool HasResumeState(Dictionary<string, object>? sessionAttributes)
    {
        return sessionAttributes != null
            && sessionAttributes.ContainsKey(ResumeStateKey);
    }

    /// <summary>
    /// Read resume state from session attributes.
    /// </summary>
    /// <param name="sessionAttributes">The session attributes dictionary.</param>
    /// <returns>The resume state, or null if not present or invalid.</returns>
    public static ResumeState? ReadState(Dictionary<string, object>? sessionAttributes)
    {
        if (!HasResumeState(sessionAttributes))
        {
            return null;
        }

        string? json = sessionAttributes![ResumeStateKey]?.ToString();
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var state = JsonConvert.DeserializeObject<ResumeState>(json);
            return string.IsNullOrEmpty(state?.ItemId) ? null : state;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

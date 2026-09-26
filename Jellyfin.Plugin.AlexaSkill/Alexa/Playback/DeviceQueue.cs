using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Represents the playback queue state for a single Echo device.
/// Persisted to disk for crash recovery across plugin restarts.
/// </summary>
public sealed class DeviceQueue
{
    /// <summary>
    /// Gets or sets the ordered list of media item IDs in the queue.
    /// </summary>
    public List<string> ItemIds { get; set; } = new();

    /// <summary>
    /// Gets or sets the zero-based index of the currently playing item.
    /// -1 means no current item.
    /// </summary>
    public int CurrentIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets the repeat mode string: "None", "One", "All".
    /// Stored as string for JSON serialization compatibility.
    /// </summary>
    public string RepeatMode { get; set; } = "None";

    /// <summary>
    /// Gets or sets the playback order: "Default" or "Shuffle".
    /// </summary>
    public string PlaybackOrder { get; set; } = "Default";

    /// <summary>
    /// Gets or sets the original (pre-shuffle) order of <see cref="ItemIds"/>.
    /// Populated by <see cref="DeviceQueueManager.ShuffleRemaining"/> so that
    /// <see cref="DeviceQueueManager.RestoreOrder"/> can revert to the original
    /// sequence on shuffle-off. Null when the queue is not currently shuffled.
    /// </summary>
    public List<string>? OriginalItemIds { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of when this queue was last modified.
    /// </summary>
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the playback position (in ticks) of the currently playing item
    /// at the time of the last pause/stop event. Used for resume-after-pause recovery.
    /// </summary>
    public long CurrentPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the item ID of the currently playing track at the time of
    /// the last pause/stop event. Used for resume-after-pause recovery.
    /// </summary>
    public string? CurrentItemId { get; set; }

    /// <summary>
    /// Gets or sets when <see cref="CurrentItemId"/> was last written (UTC).
    /// JF-619: the resume truth-source resolver compares this against
    /// <see cref="LastPlayedWrittenAt"/> so both resume entry points (the
    /// LaunchRequest offer and bare AMAZON.ResumeIntent) pick the SAME, freshest
    /// device pointer instead of each reading its own store. Null on queues
    /// persisted before JF-619: the resolver treats unknown stamps as the legacy
    /// tie (the audio-biased queue pointer wins), so old files behave unchanged.
    /// Written exclusively through <see cref="SetCurrentItemPointer"/> (the stop and
    /// nearly-finished event handlers, RecordNowPlaying), never by hand.
    /// </summary>
    public DateTime? CurrentItemWrittenAt { get; set; }

    /// <summary>
    /// Gets or sets when <see cref="LastPlayedItemId"/> was last written (UTC);
    /// the JF-619 counterpart of <see cref="CurrentItemWrittenAt"/>. Stamped by
    /// <c>DeviceQueueManager.RecordLastPlayed</c>. Null on pre-JF-619 files.
    /// </summary>
    public DateTime? LastPlayedWrittenAt { get; set; }

    /// <summary>
    /// The ONE writer for the current-item pointer (JF-619): sets the id, optionally
    /// the position, and the freshness stamp together, so a future writer cannot
    /// update the pointer without stamping it (an unstamped write would silently
    /// lose freshness arbitration to an older LastPlayed record).
    /// </summary>
    /// <param name="itemId">The item id becoming the device's current pointer.</param>
    /// <param name="positionTicks">The position when known; null leaves the stored position untouched.</param>
    public void SetCurrentItemPointer(string itemId, long? positionTicks = null)
    {
        CurrentItemId = itemId;
        if (positionTicks.HasValue)
        {
            CurrentPositionTicks = positionTicks.Value;
        }

        CurrentItemWrittenAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets or sets per-item position state (itemId → ticks). Survives item switches
    /// and bypasses Jellyfin's MinAudiobookResume threshold. Used for alternate-resume:
    /// play A, switch to B, return to A → A resumes from saved position.
    /// </summary>
    public Dictionary<string, long> ItemPositionState { get; set; } = new();

    /// <summary>
    /// Gets or sets per-item ACTIVE audio-launch bases (itemId ("N" format) →
    /// milliseconds, JF-522). A LAUNCH-SCOPED record (replacing the JF-514 last-resolve
    /// ledger, deleted by JF-522): it is written when an <c>AudioPlayer.Play</c>
    /// DIRECTIVE is issued (the BuildAudioPlayerResponse chokepoint; the sleep-timer
    /// re-issue, which builds its directive directly, records its base-0 replay
    /// itself), never by a mere resolve (precompute), so it carries the base of the
    /// stream the device is actually playing. The playback event writers add it to
    /// raw device offsets to persist item-absolute positions. Survives queue resets
    /// like the sibling stores.
    /// </summary>
    public Dictionary<string, long> ActiveLaunchBaseMs { get; set; } = new();

    /// <summary>
    /// Gets or sets per-item PENDING audio-launch bases (itemId ("N" format) →
    /// milliseconds, JF-522): enqueued directives whose stream has not started yet. A
    /// wrapped/repeat-one queue enqueues the SAME item that is still playing; the
    /// pending entry keeps that enqueue's base separate from the running stream's
    /// <see cref="ActiveLaunchBaseMs"/> entry until PlaybackStarted promotes it, so
    /// the running stream's terminal events still compose with the base they were
    /// launched with (the clobber hazard the JF-521 rejection documented).
    /// </summary>
    public Dictionary<string, long> PendingLaunchBaseMs { get; set; } = new();

    /// <summary>
    /// Gets or sets per-item ACTIVE playback rates (itemId ("N" format) → per-mille,
    /// JF-636: 750..2000), the rate half of the launch scope the base maps own. An
    /// atempo stream's output timeline runs at rate R, so the playback event writers
    /// scale raw device offsets by this value before composing the launch base
    /// (content = base + offset x R). Written ONLY beside
    /// <see cref="ActiveLaunchBaseMs"/>/<see cref="PendingLaunchBaseMs"/> by
    /// <c>DeviceQueueManager.RecordLaunchBase</c> (default 1000 = identity, no
    /// scaling), so an absent entry means a pre-JF-636 raw-static or transcode
    /// stream whose offsets never scale. Additive in the persisted JSON (missing on
    /// pre-JF-636 files).
    /// </summary>
    public Dictionary<string, int> ActivePlaybackRatePerMille { get; set; } = new();

    /// <summary>
    /// Gets or sets per-item PENDING playback rates (the enqueued twin of
    /// <see cref="ActivePlaybackRatePerMille"/>): promoted to active at the item's
    /// next PlaybackStarted together with its pending launch base.
    /// </summary>
    public Dictionary<string, int> PendingPlaybackRatePerMille { get; set; } = new();

    /// <summary>
    /// Gets or sets the item ID of the last user-initiated play on this device.
    /// Recorded at two sites: audio (incl. audiobooks, with chapter precision) in
    /// <c>PlaybackLaunchBuilder.BuildAudioPlayerResponse</c>, and video (movies/episodes) in
    /// <c>LastPlayedResponseInterceptor</c> (the response-pipeline chokepoint that catches
    /// VideoApp.Launch plays bypassing BuildAudioPlayerResponse). Unlike
    /// <c>context.AudioPlayer.Token</c>, this is updated by VideoApp.Launch plays too,
    /// making it the reliable device-specific source of truth for "what did this Echo
    /// last play" when NativeControlsForAudio routes playback through VideoApp.
    /// </summary>
    public string? LastPlayedItemId { get; set; }

    /// <summary>
    /// Gets or sets the launch route of the last user-initiated play (the
    /// <see cref="DeviceQueueManager.LaunchRoute"/> name: "Audio" or "VideoApp"; JF-568).
    /// Written by the SAME sites that write <see cref="LastPlayedItemId"/>, so the
    /// medium classification can distinguish a video-KIND item the skill launched
    /// through AudioPlayer (the JF-507 audio-only transcode, the JF-589 audio-route
    /// episodes, flat-audio books) from one it launched through VideoApp. Stored as
    /// the enum NAME string for JSON serialization compatibility (the RepeatMode
    /// shape). Null on queues persisted before JF-568: readers treat null as legacy
    /// and keep the item-kind classification, so old files behave unchanged. Purely
    /// additive in the persisted JSON: an older plugin loading a newer file ignores
    /// the unknown property (System.Text.Json skips unknown members) instead of
    /// crashing, and a newer plugin loading an older file reads null.
    /// </summary>
    public string? LastPlayedLaunchRoute { get; set; }
}

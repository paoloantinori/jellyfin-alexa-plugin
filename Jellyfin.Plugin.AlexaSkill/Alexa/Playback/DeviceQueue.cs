using System;
using System.Collections.Generic;
using System.Text.Json;
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
    /// Gets or sets the JF-812 KIND stamps beside <see cref="ItemPositionState"/>
    /// (itemId ("N" format) → kind name, the
    /// <see cref="DeviceQueueManager.PositionKindBook"/>/
    /// <see cref="DeviceQueueManager.PositionKindOther"/> constants): "Book" for a
    /// book-shaped stopped item, "Other" for every resolved non-book item (plain
    /// songs, audio-route episodes), written only by the stop path's
    /// <see cref="DeviceQueueManager.RecordStoppedPositionAndTrim"/> and only when
    /// the item RESOLVED. An entry with no kind stamp is KINDLESS (every entry on a
    /// pre-JF-812 store, and a write whose item could not be resolved); the JF-797
    /// deep-resume valve counts kindless entries as book-shaped (today's behavior,
    /// the conservative release), so old stores keep the JF-581 resume guarantee.
    /// Purely additive in the persisted JSON: pre-JF-812 files deserialize with this
    /// map empty, and the keys stay a subset of the surviving position keys (the
    /// write and the trim prune together).
    /// </summary>
    public Dictionary<string, string> ItemPositionKinds { get; set; } = new();

    /// <summary>
    /// Gets or sets per-item ACTIVE launch scopes (itemId ("N" format) →
    /// <see cref="LaunchScope"/>, JF-522/JF-648). A LAUNCH-SCOPED record (replacing
    /// the JF-514 last-resolve ledger, deleted by JF-522): it is written when an
    /// <c>AudioPlayer.Play</c> DIRECTIVE is issued (the BuildAudioPlayerResponse
    /// chokepoint; the sleep-timer re-issue, which builds its directive directly,
    /// records its base-0 replay itself), never by a mere resolve (precompute), so
    /// it carries the base and rate of the stream the device is actually playing.
    /// The playback event writers add the base to raw device offsets (scaling by the
    /// rate) to persist item-absolute positions. Survives queue resets like the
    /// sibling stores. PERSISTED SHAPE (JF-648): this map replaces the legacy
    /// activeLaunchBaseMs/activePlaybackRatePerMille half-map pair in the queue
    /// files; old-shape files fold into it at load.
    /// </summary>
    public Dictionary<string, LaunchScope> ActiveLaunchScopes { get; set; } = new();

    /// <summary>
    /// Gets or sets per-item PENDING launch scopes (itemId ("N" format) →
    /// <see cref="LaunchScope"/>, JF-522/JF-648): enqueued directives whose stream
    /// has not started yet. A wrapped/repeat-one queue enqueues the SAME item that
    /// is still playing; the pending scope keeps that enqueue's base and rate
    /// separate from the running stream's <see cref="ActiveLaunchScopes"/> entry
    /// until PlaybackStarted promotes it, so the running stream's terminal events
    /// still compose with the base they were launched with (the clobber hazard the
    /// JF-521 rejection documented). PERSISTED SHAPE (JF-648): replaces the legacy
    /// pendingLaunchBaseMs/pendingPlaybackRatePerMille half-map pair in the queue
    /// files; old-shape files fold into it at load.
    /// </summary>
    public Dictionary<string, LaunchScope> PendingLaunchScopes { get; set; } = new();

    /// <summary>
    /// JF-648 migration buffer: every JSON member this version does not bind to a
    /// declared property lands here at DESERIALIZATION time (the four legacy
    /// half-map members on an old queue file, plus any member a NEWER plugin wrote
    /// that this one does not know). Consumed and nulled by
    /// <see cref="FoldLegacyLaunchScopeMaps"/> on the manager's load path (the ONE
    /// deserialization site), so unknown members are dropped on the next persist
    /// exactly as the pre-JF-648 reader skipped them (no forward-member
    /// resurrection) and nothing here is ever re-serialized.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? LegacyAndUnknownMembers { get; set; }

    /// <summary>
    /// JF-648 migration: folds the legacy four half-map members (read out of
    /// <see cref="LegacyAndUnknownMembers"/>) into the scope-per-key maps, then
    /// nulls the buffer so its members can neither be re-read nor re-serialized.
    /// CONFLICT BY INSPECTION (a file carrying BOTH shapes, only possible from a
    /// hand-edited or foreign-written file: the plugin writes exactly one shape
    /// per version): where both shapes name a key, the SCOPE entry wins, because
    /// it was written by strictly newer code than the half-map members. BOUNDED
    /// RESIDUAL on the same hybrid files (code-review F1, JF-648): the win is
    /// per-key VALUE preference only; the folded legacy keys append at LATER
    /// insertion slots than the deserialized scope entries, so a cap-pressure
    /// trim on a hybrid file may evict scope entries before folded legacy ones
    /// (the trust order inverted by eviction order). Not worth machinery for a
    /// shape no plugin writes; the readers never see a SPLIT either way (both
    /// halves of any surviving entry stay paired). An
    /// orphan legacy RATE (a rate key with no base key; the invariant's
    /// forbidden direction, which no plugin writer produced but a torn file
    /// could carry) is DROPPED: the fold is where the half-map convention
    /// becomes the structural invariant, and carrying the orphan over would
    /// preserve exactly the corruption JF-648 closes.
    /// </summary>
    internal void FoldLegacyLaunchScopeMaps()
    {
        // A file can carry an explicit null scope member (torn or hand-edited;
        // the deserializer binds null over the property initializer) and every
        // reader assumes non-null maps, so re-materialize before anything else.
        // This also covers a NEW-shape file whose only defect is the null
        // member: the extension-data guard below must not skip it.
        ActiveLaunchScopes ??= new Dictionary<string, LaunchScope>();
        PendingLaunchScopes ??= new Dictionary<string, LaunchScope>();

        if (LegacyAndUnknownMembers == null)
        {
            return;
        }

        FoldLegacyLaunchFamily(
            ReadLegacyBaseMap(LegacyAndUnknownMembers, "activeLaunchBaseMs"),
            ReadLegacyRateMap(LegacyAndUnknownMembers, "activePlaybackRatePerMille"),
            ActiveLaunchScopes);
        FoldLegacyLaunchFamily(
            ReadLegacyBaseMap(LegacyAndUnknownMembers, "pendingLaunchBaseMs"),
            ReadLegacyRateMap(LegacyAndUnknownMembers, "pendingPlaybackRatePerMille"),
            PendingLaunchScopes);
        LegacyAndUnknownMembers = null;
    }

    /// <summary>
    /// Reads one legacy base half-map member out of the extension-data buffer
    /// (null when the member is absent or not an object). TOTAL BY DESIGN
    /// (code-review F2, JF-648): an entry that does not parse as a whole
    /// long (a torn file's fraction or overflow; the pre-JF-648 reader let
    /// the bind exception drop the WHOLE queue file at load) is SKIPPED, so
    /// one bad launch-scope entry can no longer cost the device its items,
    /// positions, and last-played record. The camelCase member names are the
    /// manager's <c>JsonNamingPolicy.CamelCase</c> persistence contract.
    /// </summary>
    private static Dictionary<string, long>? ReadLegacyBaseMap(Dictionary<string, JsonElement> data, string memberName)
    {
        if (!data.TryGetValue(memberName, out JsonElement member) || member.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var halfMap = new Dictionary<string, long>();
        foreach (JsonProperty entry in member.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt64(out long value))
            {
                halfMap[entry.Name] = value;
            }
        }

        return halfMap;
    }

    /// <summary>
    /// The rate half of <see cref="ReadLegacyBaseMap"/> (per-mille ints, same
    /// total-by-design skip of unconvertible entries; see that doc).
    /// </summary>
    private static Dictionary<string, int>? ReadLegacyRateMap(Dictionary<string, JsonElement> data, string memberName)
    {
        if (!data.TryGetValue(memberName, out JsonElement member) || member.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var halfMap = new Dictionary<string, int>();
        foreach (JsonProperty entry in member.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt32(out int value))
            {
                halfMap[entry.Name] = value;
            }
        }

        return halfMap;
    }

    /// <summary>
    /// One family's fold leg: every legacy base key becomes a scope (rate null
    /// when the rate half-map lacks the key, the tolerated pre-JF-636 shape);
    /// scope keys already present keep their scope (the conflict rule on the
    /// <see cref="FoldLegacyLaunchScopeMaps"/> doc).
    /// </summary>
    private static void FoldLegacyLaunchFamily(
        Dictionary<string, long>? legacyBase,
        Dictionary<string, int>? legacyRate,
        Dictionary<string, LaunchScope> scopes)
    {
        if (legacyBase == null)
        {
            return;
        }

        foreach (KeyValuePair<string, long> kvp in legacyBase)
        {
            if (scopes.ContainsKey(kvp.Key))
            {
                continue;
            }

            scopes[kvp.Key] = new LaunchScope
            {
                BaseMs = kvp.Value,
                RatePerMille = legacyRate != null && legacyRate.TryGetValue(kvp.Key, out int rate) ? rate : null,
            };
        }
    }

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

    /// <summary>
    /// Gets or sets the AudioPlayer token of the stream the last Enqueue directive
    /// was issued AFTER (JF-691): the durable twin of the directive's
    /// <c>ExpectedPreviousToken</c>. The ONE writer is the
    /// <c>PlaybackLaunchBuilder.BuildAudioPlayerResponse</c> chokepoint; the reader
    /// and the full veto rationale live on
    /// <c>PlaybackFinishedEventHandler.EnqueuedForThisBoundary</c>. Null when no
    /// Enqueue directive was ever issued for this device. Purely additive in the
    /// persisted JSON (the LastPlayedLaunchRoute compat contract above).
    /// </summary>
    public string? LastEnqueueAfterToken { get; set; }

    /// <summary>
    /// Gets or sets the item ID the last Enqueue directive carried: the JF-691
    /// record's diagnosability half, so the Finished veto's debug line can name
    /// what the stale record points at without a disk dig.
    /// </summary>
    public string? LastEnqueueNextItemId { get; set; }
}

/// <summary>
/// JF-648: ONE item's launch scope in ONE family (pending or active), the
/// item-absolute launch base and its playback rate as a SINGLE value (the
/// value type of <see cref="DeviceQueue.ActiveLaunchScopes"/> and
/// <see cref="DeviceQueue.PendingLaunchScopes"/>). The pre-JF-648 shape
/// carried the halves in FOUR sibling half-maps, and the trim's independent
/// per-map eviction could SPLIT a pair whose halves entered at different
/// times (a legacy mixed-age file near the cap: base evicted, rate
/// orphaned); with the halves sharing one dictionary entry the pairing
/// invariant ("a rate half exists iff its base half exists", JF-636/JF-637)
/// is STRUCTURAL: no write, trim, reset-carry, or promote can remove one
/// half without the other.
/// <see cref="LaunchScope.RatePerMille"/> is nullable for the ONE tolerated
/// legacy direction (JF-637): a pre-JF-636 entry persisted with a base and
/// no rate, folded with a null rate by
/// <see cref="DeviceQueue.FoldLegacyLaunchScopeMaps"/>. The fold preserves
/// null rather than normalizing to 1000 because a pre-JF-636 entry read null
/// before the collapse and must keep reading null after it (most readers
/// compose no scaling for null and 1000 alike, but SetPlaybackSpeed's
/// cycle-step seeds from the user's standing preference on null), and
/// promotion defaults null to 1000 (the documented JF-636 behavior). The
/// REVERSE direction (a rate over no base) never existed in a written file
/// and is dropped by the fold.
/// </summary>
public sealed class LaunchScope
{
    /// <summary>Gets or sets the item-absolute launch base in milliseconds.</summary>
    public long BaseMs { get; set; }

    /// <summary>
    /// Gets or sets the stream's playback rate in per-mille form (1000 =
    /// identity; 750..2000 = an atempo stream). Null is the tolerated
    /// pre-JF-636 legacy shape (see the class doc).
    /// </summary>
    public int? RatePerMille { get; set; }
}

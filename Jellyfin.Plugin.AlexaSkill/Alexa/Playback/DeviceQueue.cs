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
    /// Gets or sets the UTC timestamp of when this queue was last modified.
    /// </summary>
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
}

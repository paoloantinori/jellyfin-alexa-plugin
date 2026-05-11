using System;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Result of a catalog sync operation.
/// </summary>
public class SyncResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the sync completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the sync operation.
    /// </summary>
    public DateTime SyncTime { get; set; }

    /// <summary>
    /// Gets or sets the number of artists synced.
    /// </summary>
    public int ArtistCount { get; set; }

    /// <summary>
    /// Gets or sets the number of albums synced.
    /// </summary>
    public int AlbumCount { get; set; }
}

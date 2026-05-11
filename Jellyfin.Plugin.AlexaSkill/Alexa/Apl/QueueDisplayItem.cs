namespace Jellyfin.Plugin.AlexaSkill.Alexa.Apl;

/// <summary>
/// Display item for APL queue rendering (kept for caller compatibility).
/// </summary>
internal class QueueDisplayItem
{
    /// <summary>
    /// Gets or sets the display title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the artist name.
    /// </summary>
    public string? Artist { get; set; }

    /// <summary>
    /// Gets or sets the album art URL.
    /// </summary>
    public string? ArtUrl { get; set; }
}

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Apl;

/// <summary>
/// Display item for generic APL list rendering (browse, search, artist queries).
/// </summary>
internal sealed record ListDisplayItem(string Title, string Id, string? Subtitle = null, string? ArtUrl = null);

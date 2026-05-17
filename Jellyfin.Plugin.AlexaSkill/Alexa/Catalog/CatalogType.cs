namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Type of catalog content.
/// </summary>
public enum CatalogType
{
    /// <summary>Music artists.</summary>
    Artist,

    /// <summary>Music albums.</summary>
    Album,

    /// <summary>Audio tracks.</summary>
    Song,

    /// <summary>TV series.</summary>
    Series,

    /// <summary>Audiobooks.</summary>
    Audiobook
}

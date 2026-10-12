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
    Audiobook,

    /// <summary>
    /// Movies (JF-874). Feeds the catalog-backed <c>MovieTitle</c> slot type that
    /// hosts the it-IT video one-shot wrapper family (the SearchQuery title span
    /// is wrapper-hostile; live A/B on scratch skill 65da1ba5, 2026-10-12).
    /// </summary>
    Movie
}

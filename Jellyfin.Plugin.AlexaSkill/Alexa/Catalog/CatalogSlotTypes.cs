#nullable enable

using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Maps catalog types to their Alexa slot type names.
/// </summary>
public static class CatalogSlotTypes
{
    /// <summary>
    /// Maps each <see cref="CatalogType"/> to its Alexa slot type name.
    /// </summary>
    public static readonly Dictionary<CatalogType, string> Names = new()
    {
        [CatalogType.Artist] = "AMAZON.Musician",
        [CatalogType.Album] = "AMAZON.Album"
    };

    /// <summary>
    /// Catalog-backed slot type names used in the interaction model.
    /// These replace the built-in AMAZON types with catalog-supplied values.
    /// </summary>
    public static readonly Dictionary<CatalogType, string> CatalogSlotTypeNames = new()
    {
        [CatalogType.Artist] = "JellyfinArtist",
        [CatalogType.Album] = "AlbumName"
    };
}

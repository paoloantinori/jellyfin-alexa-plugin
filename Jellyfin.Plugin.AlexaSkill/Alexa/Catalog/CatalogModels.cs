#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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
    Song
}

/// <summary>
/// A single entry in a SMAPI catalog (one artist, album, or song).
/// </summary>
public class CatalogValue
{
    /// <summary>
    /// Gets or sets the canonical ID for this catalog entry.
    /// Format: <c>jellyfin_{type}_{guid_no_hyphens}</c>.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name and optional synonyms.
    /// </summary>
    [JsonPropertyName("name")]
    public CatalogValueName Name { get; set; } = new();

    /// <summary>
    /// Format a catalog ID from a catalog type and Jellyfin item GUID.
    /// </summary>
    /// <param name="type">The catalog type (artist, album, song).</param>
    /// <param name="itemId">The Jellyfin item GUID.</param>
    /// <returns>A string like <c>jellyfin_artist_a1b2c3d4e5f6...</c>.</returns>
    public static string FormatId(CatalogType type, Guid itemId)
    {
        return $"jellyfin_{type.ToString().ToLowerInvariant()}_{itemId:N}";
    }
}

/// <summary>
/// The display name and phonetic synonyms for a catalog value.
/// </summary>
public class CatalogValueName
{
    /// <summary>
    /// Gets or sets the canonical display value (e.g. "Queen").
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional phonetic synonyms for improved recognition.
    /// </summary>
    [JsonPropertyName("synonyms")]
    public List<string>? Synonyms { get; set; }
}

/// <summary>
/// The full payload uploaded to SMAPI for a catalog version.
/// </summary>
public class CatalogPayload
{
    /// <summary>
    /// Gets or sets the list of catalog values.
    /// </summary>
    [JsonPropertyName("values")]
    public List<CatalogValue> Values { get; set; } = [];

    /// <summary>
    /// Build a catalog payload from a collection of Jellyfin library items.
    /// </summary>
    /// <param name="type">The catalog type (artist, album, song).</param>
    /// <param name="items">Collection of (Id, Name) tuples from the library.</param>
    /// <param name="synonymGenerator">Function that generates phonetic synonyms for a name.</param>
    /// <returns>A populated <see cref="CatalogPayload"/>.</returns>
    public static CatalogPayload FromItems(
        CatalogType type,
        IEnumerable<(Guid Id, string Name)> items,
        Func<string, List<string>> synonymGenerator)
    {
        var payload = new CatalogPayload();

        foreach ((Guid id, string name) in items)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            List<string> synonyms = synonymGenerator(name);
            var catalogValue = new CatalogValue
            {
                Id = CatalogValue.FormatId(type, id),
                Name = new CatalogValueName
                {
                    Value = name,
                    Synonyms = synonyms.Count > 0 ? synonyms : null
                }
            };

            payload.Values.Add(catalogValue);
        }

        return payload;
    }
}

#nullable enable

using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

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

#nullable enable

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

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
#pragma warning disable CA1002, CA2227 // List required for JSON serialization
    public List<string>? Synonyms { get; set; }
#pragma warning restore CA1002, CA2227
}

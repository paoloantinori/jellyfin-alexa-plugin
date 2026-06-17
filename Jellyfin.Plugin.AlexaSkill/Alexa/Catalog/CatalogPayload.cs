#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// The full payload uploaded to SMAPI for a catalog version.
/// </summary>
public class CatalogPayload
{
    /// <summary>
    /// Gets or sets the list of catalog values.
    /// </summary>
    [JsonPropertyName("values")]
#pragma warning disable CA1002, CA2227 // List required for JSON serialization
    public List<CatalogValue> Values { get; set; } = [];
#pragma warning restore CA1002, CA2227

    /// <summary>
    /// Build a catalog payload from a collection of Jellyfin library items.
    /// </summary>
    /// <param name="type">The catalog type (artist, album, song).</param>
    /// <param name="items">Collection of (Id, Name) tuples from the library.</param>
    /// <param name="synonymGenerator">Function that generates phonetic synonyms for a name given a locale.</param>
    /// <param name="locale">The Alexa locale for phonetic synonym generation.</param>
    /// <returns>A populated <see cref="CatalogPayload"/>.</returns>
    public static CatalogPayload FromItems(
        CatalogType type,
        IEnumerable<(Guid Id, string Name)> items,
        Func<string, string, List<string>> synonymGenerator,
        string locale)
    {
        var payload = new CatalogPayload();

        foreach ((Guid id, string name) in items)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            List<string> synonyms = synonymGenerator(name, locale);
            var catalogValue = new CatalogValue
            {
                Id = CatalogValue.FormatId(type, id),
                Name = new CatalogValueName
                {
                    Value = SlotValueHelper.Truncate(name),
                    Synonyms = synonyms.Count > 0 ? synonyms.Select(SlotValueHelper.Truncate).ToList() : null
                }
            };

            payload.Values.Add(catalogValue);
        }

        return payload;
    }
}

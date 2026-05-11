using System.Collections.Generic;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;

/// <summary>
/// The display name and phonetic synonyms for a dynamic entity value.
/// </summary>
public class DynamicSlotValueName
{
    /// <summary>
    /// Gets or sets the canonical display value.
    /// </summary>
    [JsonProperty("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional synonyms for improved recognition.
    /// </summary>
    [JsonProperty("synonyms", NullValueHandling = NullValueHandling.Ignore)]
#pragma warning disable CA1002, CA2227 // List<T> and setter required for JSON serialization
    public List<string>? Synonyms { get; set; }
#pragma warning restore CA1002, CA2227
}

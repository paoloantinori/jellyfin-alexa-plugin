using System.Collections.Generic;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;

/// <summary>
/// A slot type with dynamic values for entity resolution.
/// </summary>
public class DynamicSlotType
{
    /// <summary>
    /// Gets or sets the slot type name (e.g. "AMAZON.Musician").
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the dynamic values for this slot type.
    /// </summary>
    [JsonProperty("values")]
#pragma warning disable CA1002, CA2227 // List<T> and setter required for JSON serialization
    public List<DynamicSlotValue> Values { get; set; } = new();
#pragma warning restore CA1002, CA2227
}

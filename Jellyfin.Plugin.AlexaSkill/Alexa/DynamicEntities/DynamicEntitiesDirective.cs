using System.Collections.Generic;
using Alexa.NET.Response;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;

/// <summary>
/// Dialog.UpdateDynamicEntities directive for injecting session-scoped
/// slot type values into the Alexa NLU at runtime.
/// These values supplement persistent catalog-based slot types with
/// recently played items for the current session only.
/// </summary>
public class DynamicEntitiesDirective : IDirective
{
    /// <inheritdoc/>
    [JsonProperty("type")]
    public string Type => "Dialog.UpdateDynamicEntities";

    /// <summary>
    /// Gets or sets the update behavior. "REPLACE" replaces all dynamic
    /// values for the specified types; "CLEAR" removes them.
    /// </summary>
    [JsonProperty("updateBehavior")]
    public string UpdateBehavior { get; set; } = "REPLACE";

    /// <summary>
    /// Gets or sets the list of slot types with their dynamic values.
    /// </summary>
    [JsonProperty("types")]
    public List<DynamicSlotType> Types { get; set; } = new();
}

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
    public List<DynamicSlotValue> Values { get; set; } = new();
}

/// <summary>
/// A single dynamic entity value with optional synonyms.
/// </summary>
public class DynamicSlotValue
{
    /// <summary>
    /// Gets or sets the entity ID.
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name and optional synonyms.
    /// </summary>
    [JsonProperty("name")]
    public DynamicSlotValueName Name { get; set; } = new();
}

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
    public List<string>? Synonyms { get; set; }
}

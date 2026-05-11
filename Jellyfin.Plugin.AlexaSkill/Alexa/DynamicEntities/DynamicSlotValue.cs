using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;

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

using System.Collections.Generic;
using Alexa.NET.Response;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;

/// <summary>
/// Dialog.UpdateDynamicEntities directive for injecting session-scoped
/// slot type values into the Alexa NLU at runtime.
/// These values REPLACE the named types' vocabulary for the current session
/// (updateBehavior REPLACE), not supplement it: a type the saved model wires to
/// a catalog supplier loses that vocabulary while this directive is in effect,
/// so catalog-wired type names must not ride here (JF-826; the builder
/// suppresses them).
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
#pragma warning disable CA1002, CA2227 // List<T> and setter required for JSON serialization
    public List<DynamicSlotType> Types { get; set; } = new();
#pragma warning restore CA1002, CA2227
}

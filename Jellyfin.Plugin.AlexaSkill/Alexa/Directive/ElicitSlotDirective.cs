using System.Collections.Generic;
using Alexa.NET.Response;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

/// <summary>
/// Custom IDirective for Dialog.ElicitSlot. Alexa.NET 1.22.0 doesn't include
/// a built-in class for this directive type. It serializes to:
/// <c>{ "type": "Dialog.ElicitSlot", "slotToElicit": "...", "updatedIntent": {...} }</c>
/// which tells Alexa to capture the user's next utterance as the specified slot value.
/// </summary>
internal sealed class ElicitSlotDirective : IDirective
{
    [JsonProperty("type")]
    public string Type => "Dialog.ElicitSlot";

    [JsonProperty("slotToElicit")]
    public string SlotToElicit { get; }

    [JsonProperty("updatedIntent")]
    public ElicitSlotIntent UpdatedIntent { get; }

    public ElicitSlotDirective(string slotToElicit, string intentName)
    {
        SlotToElicit = slotToElicit;
        UpdatedIntent = new ElicitSlotIntent(intentName, slotToElicit);
    }
}

/// <summary>
/// Lightweight intent representation for the ElicitSlotDirective's updatedIntent field.
/// Uses plain POCOs to avoid coupling to Alexa.NET.Request.Intent.
/// </summary>
internal sealed class ElicitSlotIntent
{
    [JsonProperty("name")]
    public string Name { get; }

    [JsonProperty("slots")]
    public Dictionary<string, ElicitSlot> Slots { get; }

    public ElicitSlotIntent(string name, string slotName)
    {
        Name = name;
        Slots = new Dictionary<string, ElicitSlot> { [slotName] = new(slotName) };
    }
}

/// <summary>
/// Lightweight slot representation for the ElicitSlotIntent's slots dictionary.
/// </summary>
internal sealed class ElicitSlot
{
    [JsonProperty("name")]
    public string Name { get; }

    public ElicitSlot(string name) => Name = name;
}

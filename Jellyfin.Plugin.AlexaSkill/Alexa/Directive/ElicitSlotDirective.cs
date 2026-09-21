using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Response;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

/// <summary>
/// Custom IDirective for Dialog.ElicitSlot. Alexa.NET 1.22.0 doesn't include
/// a built-in class for this directive type. It serializes to:
/// <c>{ "type": "Dialog.ElicitSlot", "slotToElicit": "...", "updatedIntent": {...} }</c>
/// which tells Alexa to capture the user's next utterance as the specified slot value.
/// Amazon requires updatedIntent to define EVERY slot of the target intent, not just the
/// elicited one (live INVALID_RESPONSE 2026-08-28 21:17: "All slots must be defined when
/// sending updated intent in the Dialog.ElicitSlot directive. Missing: album" when
/// eliciting musician on the two-slot PlayAlbumIntent); pass them all via the
/// <c>allSlotNames</c> constructor argument.
/// </summary>
internal sealed class ElicitSlotDirective : IDirective
{
    [JsonProperty("type")]
    public string Type => "Dialog.ElicitSlot";

    [JsonProperty("slotToElicit")]
    public string SlotToElicit { get; }

    [JsonProperty("updatedIntent")]
    public ElicitSlotIntent UpdatedIntent { get; }

    public ElicitSlotDirective(string slotToElicit, string intentName, string[]? allSlotNames = null, Dictionary<string, string?>? slotValues = null)
    {
        SlotToElicit = slotToElicit;
        UpdatedIntent = new ElicitSlotIntent(intentName, allSlotNames ?? new[] { slotToElicit }, slotValues);
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

    public ElicitSlotIntent(string name, string[] slotNames, Dictionary<string, string?>? slotValues = null)
    {
        Name = name;
        Slots = slotNames.ToDictionary(
            slotName => slotName,
            slotName => new ElicitSlot(
                slotName,
                slotValues != null && slotValues.TryGetValue(slotName, out string? value) ? value : null));
    }
}

/// <summary>
/// Lightweight slot representation for the ElicitSlotIntent's slots dictionary.
/// </summary>
internal sealed class ElicitSlot
{
    [JsonProperty("name")]
    public string Name { get; }

    /// <summary>
    /// The slot's CURRENT value, when the caller holds one: a multi-turn elicit
    /// chain must echo already-filled slots back or Amazon may treat the
    /// value-less updatedIntent as dialog-state replacement and wipe them
    /// (JF-614 review: no prior two-slot elicit chain existed to prove
    /// otherwise). Serialized only when present.
    /// </summary>
    [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)]
    public string? Value { get; }

    [JsonProperty("confirmationStatus", NullValueHandling = NullValueHandling.Ignore)]
    public string? ConfirmationStatus { get; }

    public ElicitSlot(string name, string? value = null)
    {
        Name = name;
        Value = value;
        ConfirmationStatus = value == null ? null : "NONE";
    }
}

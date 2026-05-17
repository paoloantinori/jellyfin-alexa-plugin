using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Apl;

/// <summary>
/// Represents an Alexa.Presentation.APL.UserEvent request.
/// Sent when a user interacts with a TouchWrapper in an APL document.
/// </summary>
public class AplUserEventRequest : Request
{
    public new string Type => "Alexa.Presentation.APL.UserEvent";

    /// <summary>
    /// Gets or sets the token of the APL document that generated the event.
    /// </summary>
    [JsonProperty("token")]
    public string? Token { get; set; }

    /// <summary>
    /// Gets or sets the arguments array from the SendEvent command.
    /// First element is typically the action name, second is the item ID.
    /// </summary>
    [JsonProperty("arguments")]
#pragma warning disable CA2227 // Collection required for JSON deserialization
    public JArray? Arguments { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// Gets or sets information about the APL component that triggered the event.
    /// </summary>
    [JsonProperty("source")]
#pragma warning disable CA2227 // Collection required for JSON deserialization
    public JObject? Source { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// Gets or sets the component values extracted by the SendEvent command.
    /// </summary>
    [JsonProperty("components")]
#pragma warning disable CA2227 // Collection required for JSON deserialization
    public JObject? Components { get; set; }
#pragma warning restore CA2227
}

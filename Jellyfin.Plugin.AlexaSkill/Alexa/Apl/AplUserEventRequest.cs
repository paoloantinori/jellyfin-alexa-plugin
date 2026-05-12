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
    /// The token of the APL document that generated the event.
    /// </summary>
    [JsonProperty("token")]
    public string? Token { get; set; }

    /// <summary>
    /// The arguments array from the SendEvent command.
    /// First element is typically the action name, second is the item ID.
    /// </summary>
    [JsonProperty("arguments")]
    public JArray? Arguments { get; set; }

    /// <summary>
    /// Information about the APL component that triggered the event.
    /// </summary>
    [JsonProperty("source")]
    public JObject? Source { get; set; }

    /// <summary>
    /// Component values extracted by the SendEvent command.
    /// </summary>
    [JsonProperty("components")]
    public JObject? Components { get; set; }
}

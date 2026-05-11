using Alexa.NET.Response;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

/// <summary>
/// Alexa.Presentation.APL.RenderDocument directive for displaying visual
/// layouts on Echo Show, Echo Spot, and Fire TV devices.
/// </summary>
public class AplRenderDocumentDirective : IDirective
{
    [JsonProperty("type")]
    public string Type => "Alexa.Presentation.APL.RenderDocument";

    [JsonProperty("token")]
    public string Token { get; set; } = "jellyfinSkill";

    [JsonProperty("document")]
#pragma warning disable CA2227 // Setter required for JSON serialization
    public JObject? Document { get; set; }

    [JsonProperty("datasources", NullValueHandling = NullValueHandling.Ignore)]
    public JObject? DataSources { get; set; }
#pragma warning restore CA2227
}

using System.Collections.Generic;
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
    public JObject? Document { get; set; }

    [JsonProperty("datasources", NullValueHandling = NullValueHandling.Ignore)]
    public JObject? DataSources { get; set; }
}

/// <summary>
/// Alexa.Presentation.APL.ExecuteCommands directive for updating an
/// already-rendered APL document (e.g., progress updates).
/// </summary>
public class AplExecuteCommandsDirective : IDirective
{
    [JsonProperty("type")]
    public string Type => "Alexa.Presentation.APL.ExecuteCommands";

    [JsonProperty("token")]
    public string Token { get; set; } = "jellyfinSkill";

    [JsonProperty("commands")]
    public List<JObject> Commands { get; set; } = new();
}

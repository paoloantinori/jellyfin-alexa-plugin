using System.Collections.Generic;
using Alexa.NET.Response;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

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
#pragma warning disable CA1002, CA2227 // List<T> and setter required for JSON serialization
    public List<JObject> Commands { get; set; } = new();
#pragma warning restore CA1002, CA2227
}

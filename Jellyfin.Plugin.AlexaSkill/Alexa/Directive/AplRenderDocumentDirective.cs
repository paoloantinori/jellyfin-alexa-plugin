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

    [JsonProperty("timeoutType", NullValueHandling = NullValueHandling.Ignore)]
    public string? TimeoutType { get; set; }

    [JsonProperty("document")]
#pragma warning disable CA2227 // Setter required for JSON serialization
    public JObject? Document { get; set; }

    [JsonProperty("datasources", NullValueHandling = NullValueHandling.Ignore)]
    public JObject? DataSources { get; set; }

    [JsonProperty("presentationSession", NullValueHandling = NullValueHandling.Ignore)]
    public PresentationSession? PresentationSession { get; set; }
#pragma warning restore CA2227
}

/// <summary>
/// Presentation session metadata for APL backstack navigation.
/// Ties multiple APL documents into a navigation flow with GoBack support.
/// </summary>
public class PresentationSession
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("skillId", NullValueHandling = NullValueHandling.Ignore)]
    public string? SkillId { get; set; }

    [JsonProperty("grantedExtensions", NullValueHandling = NullValueHandling.Ignore)]
    public List<GrantedExtension>? GrantedExtensions { get; set; }
}

/// <summary>
/// An extension granted for use within a presentation session.
/// </summary>
public class GrantedExtension
{
    [JsonProperty("uri")]
    public string Uri { get; set; } = string.Empty;
}

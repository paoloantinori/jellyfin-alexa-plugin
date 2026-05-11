using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

/// <summary>
/// Optional metadata for the video item displayed on screen.
/// </summary>
public class VideoItemMetadata
{
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;
}

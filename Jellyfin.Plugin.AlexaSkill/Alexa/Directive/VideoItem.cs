using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

/// <summary>
/// Video item containing the source URL and optional metadata.
/// </summary>
public class VideoItem
{
    [JsonProperty("source")]
    public string Source { get; set; } = string.Empty;

    [JsonProperty("metadata", NullValueHandling = NullValueHandling.Ignore)]
    public VideoItemMetadata? Metadata { get; set; }
}

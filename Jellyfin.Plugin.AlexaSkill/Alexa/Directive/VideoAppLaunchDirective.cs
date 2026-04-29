using System.Collections.Generic;
using Alexa.NET.Response;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

/// <summary>
/// VideoApp.Launch directive for launching video playback on Echo Show devices.
/// </summary>
public class VideoAppLaunchDirective : IDirective
{
    [JsonProperty("type")]
    public string Type => "VideoApp.Launch";

    [JsonProperty("videoItem")]
    public VideoItem? VideoItem { get; set; }
}

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

/// <summary>
/// Optional metadata for the video item displayed on screen.
/// </summary>
public class VideoItemMetadata
{
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;
}

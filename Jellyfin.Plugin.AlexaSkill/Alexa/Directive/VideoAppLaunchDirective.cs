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

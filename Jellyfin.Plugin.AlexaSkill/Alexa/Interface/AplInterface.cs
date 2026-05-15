using Alexa.NET.Management.Api;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Interface;

/// <summary>
/// ALEXA_PRESENTATION_APL interface for custom api interface.
/// Required for APL visual templates on Echo Show and Fire TV devices.
/// </summary>
public class AplInterface : CustomApiInterface
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AplInterface"/> class.
    /// </summary>
    public AplInterface()
    {
    }

    /// <summary>
    /// Gets the type of the interface.
    /// </summary>
    [JsonProperty("type")]
    public override string Type { get; } = "ALEXA_PRESENTATION_APL";
}

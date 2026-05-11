using System;
using System.Collections.Generic;
using Alexa.NET.Management;
using Alexa.NET.Management.Api;
using Alexa.NET.Management.Internals;
using Alexa.NET.Management.Manifest;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa.Interface;
using Jellyfin.Plugin.AlexaSkill.Controller;
using ManifestLocale = Alexa.NET.Management.Manifest.Locale;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;

/// <summary>
/// Represents a Alexa skill.
/// </summary>
public class ManifestSkill : Skill
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestSkill"/> class.
    /// </summary>
    /// <param name="ressourcePath">Path to the manifest ressource.</param>
    /// <param name="serverAddress">Server address.</param>
    /// <param name="sslCertType">SSL certificate type.</param>
    public ManifestSkill(string ressourcePath, string serverAddress, SslCertificateType sslCertType)
    {
        CustomApiInterfaceConverter.InterfaceLookup = new Dictionary<string, Func<CustomApiInterface>>
        {
            { "ALEXA_EXTENSION", () => new ExtensionInterface() },
            { "AUDIO_PLAYER", () => new AudioPlayerInterface() },
            { "VIDEO_APP", () => new VideoAppInterface() }
        };

        Manifest = Util.DeserializeFromFile<Skill>(ressourcePath).Manifest;

        SetApiEndpoint(serverAddress, sslCertType);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestSkill"/> class.
    /// </summary>
    /// <param name="manifest">Manifest of the skill.</param>
    /// <param name="serverAddress">Server address.</param>
    /// <param name="sslCertType">SSL certificate type.</param>
    public ManifestSkill(SkillManifest manifest, string serverAddress, SslCertificateType sslCertType)
    {
        CustomApiInterfaceConverter.InterfaceLookup = new Dictionary<string, Func<CustomApiInterface>>
        {
            { "ALEXA_EXTENSION", () => new ExtensionInterface() },
            { "AUDIO_PLAYER", () => new AudioPlayerInterface() },
            { "VIDEO_APP", () => new VideoAppInterface() }
        };

        Manifest = manifest;

        SetApiEndpoint(serverAddress, sslCertType);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestSkill"/> class.
    /// </summary>
    /// <param name="manifest">Manifest of the skill.</param>
    public ManifestSkill(SkillManifest manifest)
    {
        CustomApiInterfaceConverter.InterfaceLookup = new Dictionary<string, Func<CustomApiInterface>>
        {
            { "ALEXA_EXTENSION", () => new ExtensionInterface() },
            { "AUDIO_PLAYER", () => new AudioPlayerInterface() },
            { "VIDEO_APP", () => new VideoAppInterface() }
        };

        Manifest = manifest;
    }

    /// <summary>
    /// Get the version of this skill from the assembly metadata.
    /// </summary>
    /// <returns>The version string.</returns>
    public string GetVersionTag()
    {
        return Util.GetVersion();
    }

    /// <summary>
    /// Set the api endpoint of the skill.
    /// </summary>
    /// <param name="uri">Uri of the api server.</param>
    /// <param name="certificateType">Certificate type of the endpoint.</param>
    public void SetApiEndpoint(string uri, SslCertificateType certificateType)
    {
        if (string.IsNullOrEmpty(uri))
        {
            // remove the endpoint if it exists
            for (int i = 0; i < Manifest.Apis.Count; i++)
            {
                if (Manifest.Apis[i] is CustomApi)
                {
                    ((CustomApi)Manifest.Apis[i]).Endpoint = null;
                    return;
                }
            }

            return;
        }

        Uri endpointUri = new Uri(new Uri(uri), AlexaSkillController.ApiBaseUri);
        string endpointUriString = new Uri(endpointUri, "alexa-request").ToString();

        foreach (IApi api in Manifest.Apis)
        {
            if (api is CustomApi)
            {
                CustomApi customApi = (CustomApi)api;
                customApi.Endpoint = new Endpoint();

                customApi.Endpoint.Uri = endpointUriString;
                customApi.Endpoint.SslCertificateType = certificateType;

                return;
            }
        }

        CustomApi newCustomApi = new CustomApi();
        newCustomApi.Endpoint = new Endpoint();
        newCustomApi.Endpoint.Uri = endpointUriString;
        newCustomApi.Endpoint.SslCertificateType = certificateType;

        Manifest.Apis.Add(newCustomApi);
    }
}
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

        Manifest = global::Jellyfin.Plugin.AlexaSkill.Util.DeserializeFromFile<Skill>(ressourcePath).Manifest;
        StampVersionTag();

        SetApiEndpoint(serverAddress, sslCertType);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestSkill"/> class.
    /// Used for cloud manifests — does NOT stamp version (cloud already has it).
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
    /// Used for cloud manifests — does NOT stamp version (cloud already has it).
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
    /// Get the version tag stored in the manifest's testingInstructions.
    /// Returns null if no version tag is found (e.g. cloud manifest from before this feature).
    /// </summary>
    /// <returns>The version string, or null.</returns>
    public string? GetVersionTag()
    {
        string? instructions = Manifest.PublishingInformation.TestingInstructions;
        if (instructions != null && instructions.StartsWith("version:", StringComparison.Ordinal))
        {
            string afterPrefix = instructions["version:".Length..];
            int spaceIndex = afterPrefix.IndexOf(' ');
            return spaceIndex >= 0 ? afterPrefix[..spaceIndex] : afterPrefix.Trim();
        }

        return null;
    }

    /// <summary>
    /// Stamp the current assembly version into the manifest's testingInstructions
    /// so it round-trips through the SMAPI cloud API for version comparison.
    /// </summary>
    public void StampVersionTag()
    {
        string version = global::Jellyfin.Plugin.AlexaSkill.Util.GetVersion();
        Manifest.PublishingInformation.TestingInstructions = $"version:{version} Say 'Alexa ask jellyfin play my favorite music' or 'Alexa ask jellyfin what's playing'";
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

                // Also set events endpoint to the same URI (required by SMAPI for proactive events)
                if (Manifest.Events?.Endpoint != null)
                {
                    Manifest.Events.Endpoint.Uri = endpointUriString;
                    Manifest.Events.Endpoint.SslCertificateType = certificateType;
                }

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
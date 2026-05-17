using System;
using Alexa.NET.Management;
using Alexa.NET.Management.Manifest;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Alexa.Manifest;

public class ManifestSkillTests
{
    [Fact]
    public void ToManifestJson_ProducesValidManifest()
    {
        var manifestSkill = new ManifestSkill(
            "Jellyfin.Plugin.AlexaSkill.Alexa.Manifest.manifest.json",
            "https://example.com",
            SslCertificateType.Wildcard);

        string json = manifestSkill.ToManifestJson();
        JObject obj = JObject.Parse(json);

        Assert.NotNull(obj["manifest"]);
        Assert.NotNull(obj["manifest"]?["apis"]?["custom"]);

        string? endpoint = obj["manifest"]?["apis"]?["custom"]?["endpoint"]?["uri"]?.ToString();
        Assert.NotNull(endpoint);
        Assert.Contains("https://example.com", endpoint);
    }

    [Fact]
    public void ToManifestJson_OmitsIconUrls()
    {
        var manifestSkill = new ManifestSkill(
            "Jellyfin.Plugin.AlexaSkill.Alexa.Manifest.manifest.json",
            "https://example.com",
            SslCertificateType.Wildcard);

        string json = manifestSkill.ToManifestJson();
        JObject obj = JObject.Parse(json);

        Assert.Null(obj["manifest"]?["publishingInformation"]?["smallIconUri"]);
        Assert.Null(obj["manifest"]?["publishingInformation"]?["largeIconUri"]);
    }
}

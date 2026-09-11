using System;
using System.Linq;
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

    [Fact]
    public void Manifest_DeclaresEveryInteractionModelLocale()
    {
        var manifestSkill = new ManifestSkill(
            "Jellyfin.Plugin.AlexaSkill.Alexa.Manifest.manifest.json",
            "https://example.com",
            SslCertificateType.Wildcard);

        // Discover the supported locales the same way production code does:
        // the embedded model_*.json resources of the plugin assembly.
        var modelLocales = global::Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels()
            .Select(model => model.Item1);

        Assert.NotEmpty(modelLocales);

        var locales = manifestSkill.Manifest.PublishingInformation?.Locales;
        Assert.NotNull(locales);

        Assert.All(modelLocales, locale => Assert.Contains(locale, locales.Keys));

        Assert.All(locales, entry =>
        {
            string locale = entry.Key;
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Name), $"{locale}: name must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Summary), $"{locale}: summary must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Description), $"{locale}: description must not be empty");
            Assert.NotEmpty(entry.Value.ExamplePhrases);
            Assert.EndsWith(
                "\n\nSource: https://github.com/infinityofspace/jellyfin-alexa-plugin",
                entry.Value.Description,
                StringComparison.Ordinal);
        });
    }
}

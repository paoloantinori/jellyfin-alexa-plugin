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
    private static ManifestSkill CreateSkill() => new(
        "Jellyfin.Plugin.AlexaSkill.Alexa.Manifest.manifest.json",
        "https://example.com",
        SslCertificateType.Wildcard);

    [Fact]
    public void ToManifestJson_ProducesValidManifest()
    {
        var manifestSkill = CreateSkill();

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
        var manifestSkill = CreateSkill();

        string json = manifestSkill.ToManifestJson();
        JObject obj = JObject.Parse(json);

        Assert.Null(obj["manifest"]?["publishingInformation"]?["smallIconUri"]);
        Assert.Null(obj["manifest"]?["publishingInformation"]?["largeIconUri"]);
    }

    [Fact]
    public void Manifest_DeclaresEveryInteractionModelLocale()
    {
        var manifestSkill = CreateSkill();

        // Discover the supported locales the same way production code does:
        // the embedded model_*.json resources of the plugin assembly.
        var modelLocales = global::Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels()
            .Select(model => model.Item1);

        Assert.NotEmpty(modelLocales);

        var locales = manifestSkill.Manifest.PublishingInformation?.Locales;
        Assert.NotNull(locales);

        // Set equality, not subset: a manifest locale without a model would advertise
        // a publishing locale that no interaction model can ever deploy for.
        Assert.Equal(
            modelLocales.OrderBy(locale => locale, StringComparer.Ordinal),
            locales.Keys.OrderBy(locale => locale, StringComparer.Ordinal));

        // The response-string resources must cover the same set too, else a
        // locale silently falls back to en-US strings at runtime.
        var responseStringLocales = typeof(global::Jellyfin.Plugin.AlexaSkill.Util).Assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith("Jellyfin.Plugin.AlexaSkill.Alexa.Locale.", StringComparison.Ordinal)
                && name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => name.Split('.')[^2]);

        Assert.Equal(
            modelLocales.OrderBy(locale => locale, StringComparer.Ordinal),
            responseStringLocales.OrderBy(locale => locale, StringComparer.Ordinal));

        Assert.All(locales, entry =>
        {
            Assert.NotNull(entry.Value);
            string locale = entry.Key;
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Name), $"{locale}: name must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Summary), $"{locale}: summary must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Description), $"{locale}: description must not be empty");
            Assert.NotEmpty(entry.Value.ExamplePhrases);
            Assert.All(entry.Value.ExamplePhrases, phrase =>
                Assert.False(string.IsNullOrWhiteSpace(phrase), $"{locale}: example phrases must not be blank"));
            Assert.EndsWith(
                "\n\nSource: https://github.com/infinityofspace/jellyfin-alexa-plugin",
                entry.Value.Description,
                StringComparison.Ordinal);
        });
    }
}

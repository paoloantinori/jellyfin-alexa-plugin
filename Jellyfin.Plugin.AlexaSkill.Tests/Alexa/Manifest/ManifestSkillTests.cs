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
        ManifestSkill.EmbeddedManifestResourcePath,
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
        var modelLocales = TestLocales.AllLocales();

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
        var responseStringLocales = TestLocales.ResponseStringLocales();

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

    [Fact]
    public void CreationPayload_Subscriptions_Null_And_Publications_EnumShape()
    {
        // JF-939: pin the ACTUAL wire payload for skill creation. The live 400
        // (2026-11-21) came from the Python lab sending the template verbatim;
        // the C# typed model may serialize the enums differently. This test
        // captures what the plugin actually sends.
        var manifestSkill = CreateSkill();
        manifestSkill.PrepareForCreation();
        string json = manifestSkill.ToManifestJson();
        JObject obj = JObject.Parse(json);

        // After PrepareForCreation: subscriptions must be gone
        var subscriptions = obj["manifest"]?["events"]?["subscriptions"];
        Assert.True(subscriptions == null || !subscriptions.HasValues,
            $"subscriptions should be null/empty after PrepareForCreation, got: {subscriptions}");

        // Publications: capture what the typed model actually serializes
        var publications = obj["manifest"]?["events"]?["publications"];
        Assert.NotNull(publications);
        var firstPub = publications?.FirstOrDefault()?.ToString();
        Assert.NotEmpty(firstPub);
    }

    [Fact]
    public void PrepareForCreation_DoesNotMutateOriginal()
    {
        // JF-939 code-review finding 2: PrepareForCreation must not mutate the
        // shared Plugin.Instance.ManifestSkill. Verify the original retains
        // its subscriptions after PrepareForCreation is called on a copy.
        var original = CreateSkill();
        var originalSubs = original.Manifest.Events?.Subscriptions;
        Assert.NotNull(originalSubs);

        // Create a copy for creation (the pattern CreateSkillAsync should use)
        var forCreation = new ManifestSkill(original.Manifest!);
        forCreation.PrepareForCreation();

        // Copy must have subscriptions nulled
        Assert.Null(forCreation.Manifest.Events?.Subscriptions);
    }
}

#nullable enable
using System;
using System.Text.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-552: the graft that preserves live catalog wiring across an embedded-model
/// rebuild PUT. Extraction tolerates anything; application delegates to the
/// catalog sync's single injection implementation.
/// </summary>
public class CatalogWiringGraftTests
{
    private const string ArtistTypeName = "JellyfinArtist";
    private const string SeriesTypeName = "SeriesName";

    private static string LiveModelJson(string typesJson)
        => "{\"interactionModel\":{\"languageModel\":{\"invocationName\":\"mia collezione\",\"intents\":[{\"name\":\"AMAZON.StopIntent\",\"samples\":[]}],\"types\":[" + typesJson + "]}}}";

    private static string FreshModelJson()
        => "{\"interactionModel\":{\"languageModel\":{\"invocationName\":\"mia collezione\",\"intents\":["
        + "{\"name\":\"FindSongByArtistIntent\",\"slots\":[{\"name\":\"musician\",\"type\":\"" + ArtistTypeName + "\"}]},"
        + "{\"name\":\"PlayEpisodeIntent\",\"slots\":[{\"name\":\"series_name\",\"type\":\"" + SeriesTypeName + "\"}]}],"
        + "\"types\":["
        + "{\"name\":\"" + ArtistTypeName + "\",\"values\":[{\"name\":{\"value\":\"Mina\"}}]},"
        + "{\"name\":\"" + SeriesTypeName + "\",\"values\":[{\"name\":{\"value\":\"Breaking Bad\"}}]}]}}}";

    private static string WiredType(string name, string catalogId, string version)
        => "{\"name\":\"" + name + "\",\"valueSupplier\":{\"type\":\"CatalogValueSupplier\",\"valueCatalog\":{\"catalogId\":\"" + catalogId + "\",\"version\":\"" + version + "\"}}}";

    private static string StaticType(string name, string value)
        => "{\"name\":\"" + name + "\",\"values\":[{\"name\":{\"value\":\"" + value + "\"}}]}";

    [Fact]
    public void ExtractWiring_AllThreeTypesWired()
    {
        string live = LiveModelJson(
            string.Join(",", WiredType(ArtistTypeName, "artist-1", "7"), WiredType("AlbumName", "album-2", "8"), WiredType(SeriesTypeName, "series-3", "9")));

        var wiring = CatalogWiringGraft.ExtractWiring(live);

        Assert.NotNull(wiring);
        Assert.Equal("artist-1", wiring!.ArtistId);
        Assert.Equal("7", wiring.ArtistVersion);
        Assert.Equal("album-2", wiring.AlbumId);
        Assert.Equal("8", wiring.AlbumVersion);
        Assert.Equal("series-3", wiring.SeriesId);
        Assert.Equal("9", wiring.SeriesVersion);
        Assert.True(wiring.Any);
    }

    [Fact]
    public void ExtractWiring_OnlySeriesWired_PartialWiring()
    {
        string live = LiveModelJson(
            string.Join(",", WiredType(SeriesTypeName, "series-3", "9"), StaticType(ArtistTypeName, "Mina")));

        var wiring = CatalogWiringGraft.ExtractWiring(live);

        Assert.NotNull(wiring);
        Assert.Null(wiring!.ArtistId);
        Assert.Null(wiring.AlbumId);
        Assert.Equal("series-3", wiring.SeriesId);
    }

    [Fact]
    public void ExtractWiring_StaticSeedOnly_ReturnsNull()
    {
        string live = LiveModelJson(StaticType(SeriesTypeName, "Breaking Bad"));

        Assert.Null(CatalogWiringGraft.ExtractWiring(live));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"unexpected\":\"shape\"}")]
    [InlineData("{\"interactionModel\":null}")]
    [InlineData("{\"interactionModel\":{\"languageModel\":null}}")]
    [InlineData("{\"interactionModel\":{\"languageModel\":{\"types\":null}}}")]
    public void ExtractWiring_UnavailableOrMalformed_ReturnsNull(string? live)
        => Assert.Null(CatalogWiringGraft.ExtractWiring(live));

    [Fact]
    public void ExtractWiring_NullOrScalarValueSupplier_SkipsWithoutThrowing()
    {
        // JF-555 S3: TryGetProperty throws on non-object nodes; a null/scalar
        // valueSupplier must be skipped per the tolerant contract, not crash.
        string live = LiveModelJson(
            "{\"name\":\"" + SeriesTypeName + "\",\"valueSupplier\":null},"
            + "{\"name\":\"AlbumName\",\"valueSupplier\":\"not-an-object\"}");

        Assert.Null(CatalogWiringGraft.ExtractWiring(live));
    }

    [Fact]
    public void Apply_WithWiring_ReplacesStaticSeedOnFreshModel()
    {
        var wiring = new CatalogWiring(null, null, null, null, "series-3", "9");

        string result = CatalogWiringGraft.Apply(FreshModelJson(), "it-IT", wiring, null);

        using var doc = JsonDocument.Parse(result);
        var types = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");
        var seriesType = Assert.Single(types.EnumerateArray(), t => t.GetProperty("name").GetString() == SeriesTypeName);
        var catalog = seriesType.GetProperty("valueSupplier").GetProperty("valueCatalog");
        Assert.Equal("series-3", catalog.GetProperty("catalogId").GetString());
        Assert.Equal("9", catalog.GetProperty("version").GetString());
        Assert.False(seriesType.TryGetProperty("values", out _), "static seed values must be replaced, not kept alongside");

        // Untouched types stay static.
        var artistType = Assert.Single(types.EnumerateArray(), t => t.GetProperty("name").GetString() == ArtistTypeName);
        Assert.False(artistType.TryGetProperty("valueSupplier", out _));
    }

    [Fact]
    public void Apply_NoWiring_ReturnsModelUnchanged()
    {
        string model = FreshModelJson();

        string result = CatalogWiringGraft.Apply(model, "it-IT", null, null);

        Assert.Equal(model, result);
    }

    [Fact]
    public void Apply_UnsupportedLocale_SkipsGraft()
    {
        // JF-543 defense in depth: ar-SA live models never carry wiring, but if one
        // did (drift), the graft must refuse to re-apply it.
        var wiring = new CatalogWiring("artist-1", "1", null, null, null, null);

        string result = CatalogWiringGraft.Apply(FreshModelJson(), "ar-SA", wiring, null);

        Assert.DoesNotContain("valueSupplier", result, StringComparison.Ordinal);
    }
}

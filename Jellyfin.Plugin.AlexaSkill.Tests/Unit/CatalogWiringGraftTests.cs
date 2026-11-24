#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Tests.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-552: the graft that preserves live catalog wiring across an embedded-model
/// rebuild PUT. Extraction tolerates anything; application delegates to the
/// catalog sync's single injection implementation. JF-727 adds the
/// keyed-extraction pins: the extraction follows the slot-type-name table.
/// </summary>
public class CatalogWiringGraftTests
{
    private const string ArtistTypeName = "JellyfinArtist";
    private const string SeriesTypeName = "SeriesName";

    private static string LiveModelJson(string typesJson)
        => "{\"interactionModel\":{\"languageModel\":{\"invocationName\":\"mia collezione\",\"intents\":["
        + "{\"name\":\"AMAZON.StopIntent\",\"samples\":[]},"
        + "{\"name\":\"PlayBookIntent\",\"slots\":[{\"name\":\"book\",\"type\":\"AudiobookTitle\"}]},"
        + "{\"name\":\"PlayMovieIntent\",\"slots\":[{\"name\":\"movie\",\"type\":\"MovieTitle\"}]}],"
        + "\"types\":[" + typesJson + "]}}}";

    /// <summary>
    /// A fresh embedded-model envelope. JF-952: the fixtures DECLARE the slot
    /// types the sync wires (book + movie here) because the universal declare
    /// check skips a catalog block for a model that declares no slot of the
    /// type; the two flags turn a declaration off to pin that skip (see
    /// Apply_MovieWiring_UndeclaredFreshModel_SkipsTheBlock).
    /// </summary>
    private static string FreshModelJson(bool declareMovie = true, bool declareBook = true)
    {
        var intents = new List<string>
        {
            "{\"name\":\"FindSongByArtistIntent\",\"slots\":[{\"name\":\"musician\",\"type\":\"" + ArtistTypeName + "\"}]}",
            "{\"name\":\"PlayEpisodeIntent\",\"slots\":[{\"name\":\"series_name\",\"type\":\"" + SeriesTypeName + "\"}]}"
        };
        if (declareMovie)
        {
            intents.Add("{\"name\":\"PlayMovieIntent\",\"slots\":[{\"name\":\"movie\",\"type\":\"MovieTitle\"}]}");
        }

        if (declareBook)
        {
            intents.Add("{\"name\":\"PlayBookIntent\",\"slots\":[{\"name\":\"book\",\"type\":\"AudiobookTitle\"}]}");
        }

        return "{\"interactionModel\":{\"languageModel\":{\"invocationName\":\"mia collezione\",\"intents\":["
        + string.Join(",", intents)
        + "],"
        + "\"types\":["
        + "{\"name\":\"" + ArtistTypeName + "\",\"values\":[{\"name\":{\"value\":\"Mina\"}}]},"
        + "{\"name\":\"" + SeriesTypeName + "\",\"values\":[{\"name\":{\"value\":\"Breaking Bad\"}}]}]}}}";
    }

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

    /// <summary>
    /// JF-727 review: a non-string scalar in a type entry's name (parseable
    /// JSON, e.g. a number) makes JsonElement.GetString() THROW
    /// InvalidOperationException, which the JsonException-only catch does not
    /// cover; the ValueKind guard must skip the entry per the tolerant
    /// contract instead. The mixed shape also proves the skip is per-entry:
    /// the well-formed sibling still extracts.
    /// </summary>
    [Fact]
    public void ExtractWiring_ScalarNameType_SkipsEntryWithoutThrowing()
    {
        string live = LiveModelJson(
            "{\"name\":123,\"valueSupplier\":{\"valueCatalog\":{\"catalogId\":\"bad\",\"version\":\"1\"}}},"
            + WiredType(SeriesTypeName, "series-3", "9"));

        var wiring = CatalogWiringGraft.ExtractWiring(live);

        Assert.NotNull(wiring);
        Assert.Equal("series-3", wiring!.SeriesId);
        Assert.Null(wiring.ArtistId);
        Assert.Null(wiring.AlbumId);
    }

    /// <summary>
    /// JF-727: the extraction is keyed off CatalogSlotTypes.CatalogSlotTypeNames
    /// (through its reverse lookup, TryGetCatalogTypeForSlotTypeName), not a hand
    /// if/else of three names. That lookup's only call site in the plugin is
    /// ExtractWiring; reverting the extraction to a per-type if/else drops the
    /// call, and with it a future synced type's wiring, and fails this pin.
    /// Uses the shared single-call-site assertion (LibrarySyncServiceStructureTests
    /// .AssertOnlyCallerIs, the JF-727 simplify hoist of that idiom).
    /// </summary>
    [Fact]
    public void ExtractWiring_IsKeyedByTheSlotTypeTable_TheReverseLookupCallSite()
    {
        MethodBase lookup = LibrarySyncServiceStructureTests.RequireMethod(
            typeof(CatalogSlotTypes),
            "TryGetCatalogTypeForSlotTypeName",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            "an internal static method");

        MethodBase expectedCaller = LibrarySyncServiceStructureTests.RequireMethod(
            typeof(CatalogWiringGraft),
            "ExtractWiring",
            BindingFlags.NonPublic | BindingFlags.Static,
            "an internal static method of CatalogWiringGraft");

        LibrarySyncServiceStructureTests.AssertOnlyCallerIs(
            typeof(CatalogWiringGraft).Assembly,
            lookup,
            "TryGetCatalogTypeForSlotTypeName",
            expectedCaller,
            "CatalogWiringGraft.ExtractWiring (the JF-727 keyed extraction)");
    }

    /// <summary>
    /// JF-727: every slot type name the sync table declares
    /// (CatalogSlotTypes.CatalogSlotTypeNames) must be extractable into a
    /// non-empty CatalogWiring. Pins the coverage contract end to end: adding
    /// a fourth synced type's table entry without extending CatalogWiring (and
    /// the positional construction in ExtractWiring, which arity forces) fails
    /// here, because that type's wiring would extract nowhere and Apply would
    /// re-PUT its rebuilt model unwired.
    /// </summary>
    [Fact]
    public void ExtractWiring_EveryCatalogSlotTypeName_ExtractsIntoWiring()
    {
        foreach (var (catalogType, slotTypeName) in CatalogSlotTypes.CatalogSlotTypeNames)
        {
            string live = LiveModelJson(WiredType(slotTypeName, "catalog-for-" + catalogType, "1"));

            var wiring = CatalogWiringGraft.ExtractWiring(live);

            Assert.True(
                wiring != null && wiring.Any,
                $"A live model wiring only {slotTypeName} ({catalogType}) must extract into a non-empty CatalogWiring: the extraction is keyed off CatalogSlotTypeNames, and the record must cover every synced type so a rebuild PUT never drops it unwired (JF-727)");
        }
    }

    [Fact]
    public void Apply_WithWiring_ReplacesStaticSeedOnFreshModel()
    {
        var wiring = new CatalogWiring(null, null, null, null, "series-3", "9", null, null, null, null);

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

    /// <summary>
    /// JF-823: an audiobook-only live wiring extracts into the new record
    /// fields and a rebuild PUT grafts it back (the ExtractWiring keyed
    /// extraction covers the name via the JF-727 reverse map; this pin holds
    /// the record's own fields end to end through Apply).
    /// </summary>
    [Fact]
    public void ExtractAndApply_AudiobookOnlyWiring_SurvivesTheRebuildPut()
    {
        const string AudiobookTypeName = "AudiobookTitle";
        string live = LiveModelJson(
            WiredType(AudiobookTypeName, "audiobook-4", "5") + "," + StaticType(ArtistTypeName, "Mina"));

        var wiring = CatalogWiringGraft.ExtractWiring(live);

        Assert.NotNull(wiring);
        Assert.Equal("audiobook-4", wiring!.AudiobookId);
        Assert.Equal("5", wiring.AudiobookVersion);
        Assert.Null(wiring.ArtistId);
        Assert.Null(wiring.SeriesId);

        string result = CatalogWiringGraft.Apply(FreshModelJson(), "it-IT", wiring, null);

        using var doc = JsonDocument.Parse(result);
        var types = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");
        var audiobookType = Assert.Single(types.EnumerateArray(), t => t.GetProperty("name").GetString() == AudiobookTypeName);
        var catalog = audiobookType.GetProperty("valueSupplier").GetProperty("valueCatalog");
        Assert.Equal("audiobook-4", catalog.GetProperty("catalogId").GetString());
        Assert.Equal("5", catalog.GetProperty("version").GetString());
        Assert.False(audiobookType.TryGetProperty("values", out _), "static seed values must be replaced, not kept alongside");
    }

    /// <summary>
    /// JF-874 + JF-952: a movie-only live wiring (a live it-IT model whose
    /// MovieTitle block carries the valueSupplier, nothing else wired)
    /// extracts into the record's Movie fields and a rebuild PUT grafts it
    /// back as the catalog supplier on a fresh model that DECLARES the type
    /// (PlayMovieIntent's MovieTitle slot): the injection adds the block
    /// there. A fresh model that declares NO MovieTitle slot takes the
    /// JF-952 skip instead, pinned right below.
    /// </summary>
    [Fact]
    public void ExtractAndApply_MovieOnlyWiring_AddsTypeOnFreshModel()
    {
        const string MovieTypeName = "MovieTitle";
        string live = LiveModelJson(WiredType(MovieTypeName, "movie-9", "2"));

        var wiring = CatalogWiringGraft.ExtractWiring(live);

        Assert.NotNull(wiring);
        Assert.Equal("movie-9", wiring!.MovieId);
        Assert.Equal("2", wiring.MovieVersion);
        Assert.Null(wiring.ArtistId);
        Assert.Null(wiring.AlbumId);

        string result = CatalogWiringGraft.Apply(FreshModelJson(), "it-IT", wiring, null);

        using var doc = JsonDocument.Parse(result);
        var types = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");
        var movieType = Assert.Single(types.EnumerateArray(), t => t.GetProperty("name").GetString() == MovieTypeName);
        var catalog = movieType.GetProperty("valueSupplier").GetProperty("valueCatalog");
        Assert.Equal("movie-9", catalog.GetProperty("catalogId").GetString());
        Assert.Equal("2", catalog.GetProperty("version").GetString());
    }

    /// <summary>
    /// JF-952: the graft rides the same universal declare check as the sync,
    /// so a rebuild PUT into a locale whose fresh model declares no MovieTitle
    /// slot no longer re-creates the inert block (the pre-JF-952 graft added
    /// the proven inert AlbumName-shaped block on every rebuild of the 16
    /// non-declaring locales once their live model carried the wiring).
    /// </summary>
    [Fact]
    public void Apply_MovieWiring_UndeclaredFreshModel_SkipsTheBlock()
    {
        const string MovieTypeName = "MovieTitle";
        string live = LiveModelJson(WiredType(MovieTypeName, "movie-9", "2"));

        var wiring = CatalogWiringGraft.ExtractWiring(live);

        Assert.NotNull(wiring);

        string result = CatalogWiringGraft.Apply(FreshModelJson(declareMovie: false, declareBook: true), "en-US", wiring, null);

        using var doc = JsonDocument.Parse(result);
        var types = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");
        Assert.DoesNotContain(types.EnumerateArray(), t => t.GetProperty("name").GetString() == MovieTypeName);
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
        var wiring = new CatalogWiring("artist-1", "1", null, null, null, null, null, null, null, null);

        string result = CatalogWiringGraft.Apply(FreshModelJson(), "ar-SA", wiring, null);

        Assert.DoesNotContain("valueSupplier", result, StringComparison.Ordinal);
    }
}

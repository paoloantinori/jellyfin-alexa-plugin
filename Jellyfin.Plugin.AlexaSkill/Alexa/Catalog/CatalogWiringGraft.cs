#nullable enable
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// JF-552: pure JSON graft of live catalog wiring onto a freshly built model
/// envelope. Extraction reads the live model's <c>types</c> array; application
/// delegates to <see cref="CatalogManager.InjectCatalogReferences"/> so the graft
/// and the catalog sync share ONE injection implementation (replace-in-place
/// semantics, stale-version warning, ReplacesType re-typing).
/// </summary>
internal static class CatalogWiringGraft
{
    /// <summary>
    /// Extracts the catalog wiring from a live interaction-model envelope.
    /// Tolerant by design: a malformed body, a missing types array, or a GET
    /// failure upstream all yield null (nothing to preserve; the PUT proceeds
    /// unwired, which is today's behavior for a first deploy).
    /// </summary>
    /// <param name="liveModelJson">The live model's JSON envelope, or null when unavailable.</param>
    /// <returns>The wiring, or null when the live model carries none.</returns>
    internal static CatalogWiring? ExtractWiring(string? liveModelJson)
    {
        if (string.IsNullOrWhiteSpace(liveModelJson))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(liveModelJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("interactionModel", out var interactionModel)
                || interactionModel.ValueKind != JsonValueKind.Object
                || !interactionModel.TryGetProperty("languageModel", out var languageModel)
                || languageModel.ValueKind != JsonValueKind.Object
                || !languageModel.TryGetProperty("types", out var types)
                || types.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? artistId = null, artistVersion = null;
            string? albumId = null, albumVersion = null;
            string? seriesId = null, seriesVersion = null;

            foreach (var type in types.EnumerateArray())
            {
                // ValueKind guards: TryGetProperty THROWS on non-object nodes, and a
                // null/scalar valueSupplier or valueCatalog is exactly the malformed
                // shape the tolerant contract must skip, not crash on (JF-555 S3).
                if (type.ValueKind != JsonValueKind.Object
                    || !type.TryGetProperty("name", out var nameEl)
                    || nameEl.GetString() is not { } typeName
                    || !type.TryGetProperty("valueSupplier", out var supplier)
                    || supplier.ValueKind != JsonValueKind.Object
                    || !supplier.TryGetProperty("valueCatalog", out var catalog)
                    || catalog.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? id = catalog.TryGetProperty("catalogId", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                string? version = catalog.TryGetProperty("version", out var versionEl) && versionEl.ValueKind == JsonValueKind.String ? versionEl.GetString() : null;

                if (typeName == CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Artist])
                {
                    artistId = id;
                    artistVersion = version;
                }
                else if (typeName == CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Album])
                {
                    albumId = id;
                    albumVersion = version;
                }
                else if (typeName == CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Series])
                {
                    seriesId = id;
                    seriesVersion = version;
                }
            }

            var wiring = new CatalogWiring(artistId, artistVersion, albumId, albumVersion, seriesId, seriesVersion);
            return wiring.Any ? wiring : null;
        }
    }

    /// <summary>
    /// Applies the wiring to a model envelope, returning the JSON to PUT. A null
    /// or empty wiring returns the model unchanged; a locale where Amazon's full
    /// build rejects catalog wiring (JF-543) also returns it unchanged (the live
    /// model there never carries wiring, so this is defense in depth).
    /// </summary>
    /// <param name="modelJson">The freshly built model envelope.</param>
    /// <param name="locale">The target locale, for the JF-543 guard.</param>
    /// <param name="wiring">The wiring extracted from the live model, or null.</param>
    /// <param name="logger">Logger for the injection's own diagnostics.</param>
    /// <returns>The model JSON to PUT (grafted when wiring exists).</returns>
    internal static string Apply(string modelJson, string locale, CatalogWiring? wiring, ILogger? logger)
    {
        if (wiring == null || !wiring.Any)
        {
            return modelJson;
        }

        if (!CatalogManager.IsCatalogWiringSupported(locale))
        {
            logger?.LogWarning(
                "Live model for {Locale} unexpectedly carries catalog wiring; refusing to re-apply it (JF-543 locale, Amazon's full build fails for catalog-wired models there)",
                locale);
            return modelJson;
        }

        logger?.LogInformation(
            "Preserving live catalog wiring on rebuild for {Locale}: artist={ArtistId}, album={AlbumId}, series={SeriesId} (JF-552)",
            locale,
            wiring.ArtistId ?? "-",
            wiring.AlbumId ?? "-",
            wiring.SeriesId ?? "-");

        return CatalogManager.InjectCatalogReferences(
            modelJson,
            wiring.ArtistId,
            wiring.AlbumId,
            wiring.SeriesId,
            wiring.ArtistVersion,
            wiring.AlbumVersion,
            wiring.SeriesVersion,
            logger);
    }
}

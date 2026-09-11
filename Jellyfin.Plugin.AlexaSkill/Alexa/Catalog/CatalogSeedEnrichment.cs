#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Merges the static seed values of the catalog-backed slot types into the SMAPI
/// catalog upload (JF-541 phase 2). The catalog supplier REPLACES the committed
/// model's static type block at deploy time (CatalogManager.InjectCatalogReferences),
/// so seed titles absent from the user's library (Thriller, Abbey Road, Queen, ...)
/// vanish from the deployed model and their one-shot utterances collapse.
/// The seeds are read from the EMBEDDED interaction model JSONs, which keeps the
/// committed models the single copy of the seed data: a C# table would duplicate
/// them (JF-316 tracks the generator-side consolidation; the values live in the
/// it-IT YAML template and the per-locale model JSONs).
/// </summary>
public static class CatalogSeedEnrichment
{
    /// <summary>
    /// The only committed model that declares <c>AlbumName</c> (JF-332: the other
    /// 16 locales use AMAZON.MusicRecording) and therefore the only one carrying
    /// the album seed list. The catalog content is shared across locales, so the
    /// it-IT seeds are the album seed authority.
    /// </summary>
    private const string AlbumSeedLocale = "it-IT";

    private static readonly Lazy<IReadOnlyList<string>> AlbumSeeds =
        new(LoadAlbumSeeds);

    private static readonly Lazy<IReadOnlyList<string>> ArtistSeeds =
        new(LoadArtistSeeds);

    /// <summary>
    /// Appends the seed values of the catalog-backed slot type to the payload.
    /// Library entries win on collision: a seed whose name matches an existing
    /// (library) value case-insensitively is skipped, so library-present titles
    /// keep their real Jellyfin ids and behavior is unchanged. Idempotent.
    /// </summary>
    /// <param name="payload">The catalog payload built from library items.</param>
    /// <param name="type">The catalog type being uploaded.</param>
    /// <param name="synonymGenerator">Function that generates phonetic synonyms for a name given a locale.</param>
    /// <param name="locale">The Alexa locale for phonetic synonym generation.</param>
    /// <param name="logger">Optional logger for the one-line audit of how many seeds were appended.</param>
    public static void MergeInto(
        CatalogPayload payload,
        CatalogType type,
        Func<string, string, List<string>> synonymGenerator,
        string locale,
        ILogger? logger = null)
    {
        IReadOnlyList<string> seeds = GetSeedNames(type);
    if (seeds.Count == 0 && logger != null)
    {
        // One-time visibility for the silent-empty degradation (embedded model
        // missing or unparseable): the enrichment disables itself for this type
        // for the process lifetime (Lazy).
        logger.LogWarning(
            "Catalog seed enrichment for {Type}: no seed values extracted from the embedded interaction models; the enrichment is disabled for this process lifetime (JF-541b)",
            type);
    }
        if (seeds.Count == 0)
        {
            return;
        }

        int before = payload.Values.Count;
        MergeSeeds(payload, type, seeds, synonymGenerator, locale);

        if (logger != null && payload.Values.Count > before)
        {
            logger.LogInformation(
                "Catalog seed enrichment for {Type}: appended {Count} static seed values from the embedded interaction models (JF-541)",
                type,
                payload.Values.Count - before);
        }
    }

    /// <summary>
    /// Resolves the seed names for a catalog type.
    /// Album: the it-IT model's AlbumName block. Artist: the union of the
    /// JellyfinArtist blocks across the committed locale models (the seeds differ
    /// per locale: it-IT carries 8, the en-* locales 10; the catalog upload is
    /// shared across locales, so the union keeps every locale's seed vocabulary
    /// on the deployed model). Series is deliberately NOT merged: its seeds are
    /// per-locale LOCALIZED titles (Juego de Tronos, Il Trono di Spade, ...) and
    /// the union-merge decision for them is out of JF-541 phase 2 scope. All
    /// other types have no seeds.
    /// </summary>
    /// <param name="type">The catalog type.</param>
    /// <returns>The seed names; empty when the type carries no seeds.</returns>
    public static IReadOnlyList<string> GetSeedNames(CatalogType type) => type switch
    {
        CatalogType.Album => AlbumSeeds.Value,
        CatalogType.Artist => ArtistSeeds.Value,
        _ => Array.Empty<string>()
    };

    /// <summary>
    /// Extracts the values of one slot type from an interaction model JSON.
    /// Tolerates both the raw <c>languageModel</c> root (the committed model_*.json
    /// files) and the SMAPI <c>interactionModel.languageModel</c> envelope.
    /// Returns an empty list when the type is absent or the JSON is malformed
    /// (seed enrichment must never fail the sync).
    /// </summary>
    /// <param name="modelJson">The interaction model JSON string.</param>
    /// <param name="slotTypeName">The slot type whose values to extract.</param>
    /// <returns>The slot type's value names, deduplicated case-insensitively.</returns>
    internal static IReadOnlyList<string> ExtractSeedNames(string modelJson, string slotTypeName)
    {
        try
        {
            using var doc = JsonDocument.Parse(modelJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            JsonElement container;
            if (root.TryGetProperty("languageModel", out var lm) && lm.ValueKind == JsonValueKind.Object)
            {
                container = lm;
            }
            else if (root.TryGetProperty("interactionModel", out var im)
                && im.ValueKind == JsonValueKind.Object
                && im.TryGetProperty("languageModel", out var lm2)
                && lm2.ValueKind == JsonValueKind.Object)
            {
                container = lm2;
            }
            else
            {
                return Array.Empty<string>();
            }

            if (!container.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            foreach (JsonElement typeNode in types.EnumerateArray())
            {
                if (typeNode.ValueKind != JsonValueKind.Object
                    || !typeNode.TryGetProperty("name", out var nameNode)
                    || !string.Equals(nameNode.GetString(), slotTypeName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!typeNode.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<string>();
                }

                return values.EnumerateArray()
                    .Select(v => v.TryGetProperty("name", out var vn)
                        && vn.ValueKind == JsonValueKind.Object
                        && vn.TryGetProperty("value", out var vv)
                            ? vv.GetString()
                            : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Appends seed values to the payload with the same shape <see cref="CatalogPayload.FromItems"/>
    /// builds for library items: truncated value, truncated synonyms, catalog id
    /// from a deterministic seed guid. Library values win on case-insensitive
    /// name collision (dedup key), and seeds are deduplicated among themselves.
    /// </summary>
    internal static void MergeSeeds(
        CatalogPayload payload,
        CatalogType type,
        IEnumerable<string> seeds,
        Func<string, string, List<string>> synonymGenerator,
        string locale)
    {
        var existing = payload.Values
            .Select(v => v.Name.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string seed in seeds)
        {
            string value = SlotValueHelper.Truncate(seed);
            if (string.IsNullOrWhiteSpace(value) || !existing.Add(value))
            {
                // Library wins on collision (its entry keeps the real Jellyfin id).
                continue;
            }

            List<string> synonyms = synonymGenerator(seed, locale);
            payload.Values.Add(new CatalogValue
            {
                Id = CatalogValue.FormatId(type, StableSeedGuid(seed)),
                Name = new CatalogValueName
                {
                    Value = value,
                    Synonyms = synonyms.Count > 0 ? synonyms.Select(SlotValueHelper.Truncate).ToList() : null
                }
            });
        }
    }

    /// <summary>
    /// Deterministic id for a seed entry (no Jellyfin item backs it): the first 16
    /// bytes of the SHA-256 of the name. Stable across sync runs so repeated
    /// uploads produce an unchanged seed block and catalog version diffs stay quiet.
    /// </summary>
    /// <param name="name">The seed name.</param>
    /// <returns>A deterministic guid derived from the name.</returns>
    internal static Guid StableSeedGuid(string name)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static IReadOnlyList<string> LoadAlbumSeeds()
    {
        return ReadModelSeeds(AlbumSeedLocale, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Album]);
    }

    private static List<string> LoadArtistSeeds()
    {
        string typeName = CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Artist];
        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string _, string resourcePath) in GetModelsSortedByLocale())
        {
            foreach (string seed in ReadResourceSeeds(resourcePath, typeName))
            {
                union.Add(seed);
            }
        }

        return union.ToList();
    }

    private static IReadOnlyList<string> ReadModelSeeds(string locale, string slotTypeName)
    {
        var match = global::Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels()
            .FirstOrDefault(m => m.Item1 == locale);
        return match == null ? Array.Empty<string>() : ReadResourceSeeds(match.Item2, slotTypeName);
    }

    private static IReadOnlyList<string> ReadResourceSeeds(string resourcePath, string slotTypeName)
    {
        try
        {
            using Stream? resource = typeof(global::Jellyfin.Plugin.AlexaSkill.Util).Assembly.GetManifestResourceStream(resourcePath);
            if (resource == null)
            {
                return Array.Empty<string>();
            }

            using var reader = new StreamReader(resource);
            return ExtractSeedNames(reader.ReadToEnd(), slotTypeName);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<(string Locale, string ResourcePath)> GetModelsSortedByLocale() =>
        global::Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels()
            .Select(m => (Locale: m.Item1, ResourcePath: m.Item2))
            .OrderBy(m => m.Locale, StringComparer.Ordinal);
}

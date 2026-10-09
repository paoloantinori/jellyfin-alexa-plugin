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
/// it-IT YAML template and the per-locale model JSONs). The ONE deliberate C#
/// duplication is <see cref="GenericAudiobookWords"/> (one generic word per
/// non-it locale, JF-823 live A/B): the per-locale table matches how the word
/// is consumed (per-leg, not union-merged) and is pinned against the templates.
/// </summary>
public static class CatalogSeedEnrichment
{
    /// <summary>
    /// The only committed model that declares <c>AlbumName</c> (JF-332: the other
    /// 16 locales use AMAZON.MusicRecording) and therefore the only one carrying
    /// the album seed list; also the only one carrying real audiobook titles
    /// (JF-823). The other 16 locales' entire AudiobookTitle vocabulary is a
    /// single generic word, and the original Series-style skip of those words
    /// was REVERSED by the JF-823 live A/B verdict (2026-10-09: post-sync,
    /// generic-word fills degraded to NO_SELECTION): each word now rides its
    /// OWN locale's leg via <see cref="GenericAudiobookWords"/>, still not a
    /// union. The catalog content is shared across locales, so the it-IT seeds
    /// are the seed authority for the title lists.
    /// </summary>
    private const string ItItSeedLocale = "it-IT";

    /// <summary>
    /// JF-823 live A/B verdict (2026-10-09): each non-it locale's AudiobookTitle
    /// vocabulary was exactly ONE generic word before the catalog wiring; the
    /// replace-in-place sync (CatalogWiringGraft) left those locales with
    /// library titles only, so the word vanished and bare generic-word requests
    /// ("lies ein hörbuch", "play an audiobook") degraded to NO_SELECTION. This
    /// table appends each locale's own word to the audiobook payload leg of that
    /// locale ONLY. Sourced from each locale's template AudiobookTitle block
    /// (do not invent; the sourcing is pinned by
    /// GenericAudiobookWords_MatchEachTemplatesAudiobookTitleValue). it-IT has
    /// no entry: its block is the 22-value title seed, not a generic word.
    /// ar-SA is inert (the JF-543 gate excludes it from all catalog traffic)
    /// but kept so the table mirrors the full 16-locale vocabulary. The key
    /// set itself is pinned (no phantom entry) by
    /// GenericAudiobookWords_KeySetMirrorsTheNonItLocaleRoster.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> GenericAudiobookWords =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ar-SA"] = "كتاب صوتي",
            ["de-DE"] = "Hörbuch",
            ["en-AU"] = "audiobook",
            ["en-CA"] = "audiobook",
            ["en-GB"] = "audiobook",
            ["en-IN"] = "audiobook",
            ["en-US"] = "audiobook",
            ["es-ES"] = "audiolibro",
            ["es-MX"] = "audiolibro",
            ["es-US"] = "audiolibro",
            ["fr-CA"] = "livre audio",
            ["fr-FR"] = "livre audio",
            ["hi-IN"] = "ऑडियोबुक",
            ["ja-JP"] = "オーディオブック",
            ["nl-NL"] = "luisterboek",
            ["pt-BR"] = "audiolivro"
        };

    private static readonly Lazy<IReadOnlyList<string>> AlbumSeeds =
        new(LoadAlbumSeeds);

    private static readonly Lazy<IReadOnlyList<string>> ArtistSeeds =
        new(LoadArtistSeeds);

    private static readonly Lazy<IReadOnlyList<string>> AudiobookSeeds =
        new(LoadAudiobookSeeds);

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

        // The per-locale generic-word arm (JF-823): the word rides THIS leg
        // only, through the same MergeSeeds path, so it stays idempotent.
        string? genericWord = type == CatalogType.Audiobook
            && GenericAudiobookWords.TryGetValue(locale, out string? word)
                ? word
                : null;

        if (seeds.Count == 0 && logger != null)
        {
            // One-time visibility for the silent-empty degradation (embedded model
            // missing or unparseable): the title-seed enrichment disables itself
            // for this type for the process lifetime (Lazy). Keyed on the MODEL
            // seeds only: an empty it-IT block with the per-locale word still
            // present is reported here even though the word keeps the leg alive.
            logger.LogWarning(
                "Catalog seed enrichment for {Type}: no seed values extracted from the embedded interaction models; the title-seed enrichment is disabled for this process lifetime (JF-541b)",
                type);
        }

        if (seeds.Count == 0 && genericWord == null)
        {
            return;
        }

        int before = payload.Values.Count;
        MergeSeeds(
            payload,
            type,
            genericWord == null ? seeds : seeds.Append(genericWord),
            synonymGenerator,
            locale);

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
    /// on the deployed model). Audiobook: the it-IT model's AudiobookTitle block
    /// (JF-823 - the 22-value seed that is the saved model's only vocabulary
    /// before the first catalog sync lands; the other locales' generic words
    /// ride their own legs via <see cref="GenericAudiobookWords"/>, see
    /// <see cref="ItItSeedLocale"/>). Series is deliberately NOT
    /// merged: its seeds are per-locale LOCALIZED titles (Juego de Tronos,
    /// Il Trono di Spade, ...) and the union-merge decision for them is out of
    /// JF-541 phase 2 scope. All other types have no seeds.
    /// </summary>
    /// <param name="type">The catalog type.</param>
    /// <returns>The seed names; empty when the type carries no seeds.</returns>
    public static IReadOnlyList<string> GetSeedNames(CatalogType type) => type switch
    {
        CatalogType.Album => AlbumSeeds.Value,
        CatalogType.Artist => ArtistSeeds.Value,
        CatalogType.Audiobook => AudiobookSeeds.Value,
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

            // Artist seeds are catalog entries like library ones, so the shared
            // factory enriches them identically (JF-684 via the JF-689 factory).
            payload.Values.Add(CatalogValueFactory.Create(type, StableSeedGuid(seed), seed, synonyms));
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
        return ReadModelSeeds(ItItSeedLocale, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Album]);
    }

    private static IReadOnlyList<string> LoadAudiobookSeeds()
    {
        return ReadModelSeeds(ItItSeedLocale, CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Audiobook]);
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

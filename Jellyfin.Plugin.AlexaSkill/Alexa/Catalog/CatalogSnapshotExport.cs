#nullable enable

using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// The authoritative catalog snapshot export (JF-954.8): locale, then slot
/// type name, then the entries the sync would upload, in upload order. The
/// serialization shape is the JF-954.1 snapshot format with producer
/// "plugin": {"schema": 1, "producer": "plugin", "locales": {"es-ES":
/// {"SeriesName": [{"value": ..., "synonyms": [...]}]}}}.
/// </summary>
/// <param name="Locales">The per-locale tables; a locale appears only when the sync would process it for the user, a type only when the sync would upload it (non-zero items) for the user.</param>
internal sealed record CatalogSnapshotExport(Dictionary<string, Dictionary<string, List<CatalogSnapshotEntry>>> Locales);

/// <summary>
/// One value-and-synonyms entry of the authoritative catalog snapshot export
/// (JF-954.8), the shape the JF-954.1 snapshot format defines
/// ({"value": ..., "synonyms": [...]}). Values and synonyms ONLY: the SMAPI
/// catalog ids, the Jellyfin item ids and every other payload field are
/// deliberately not carried, so the export response can never leak anything
/// beyond what Alexa trains on.
/// </summary>
/// <param name="Value">The canonical slot value exactly as the sync would upload it (truncation and all).</param>
/// <param name="Synonyms">The value's synonym family; empty when the entry carries none.</param>
internal sealed record CatalogSnapshotEntry(string Value, IReadOnlyList<string> Synonyms);

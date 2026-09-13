#nullable enable
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// The catalog wiring extracted from a live interaction model: the
/// valueSupplier/valueCatalog id and version for each catalog-backed slot type
/// (JF-552). Used to re-apply the wiring a freshly built model would otherwise
/// drop: <c>Plugin.BuildSkillInteractionModels</c> produces models from the
/// embedded resources, and the Alexa.NET typed model cannot carry
/// <c>valueCatalog</c> at all (a round-trip silently drops it, probed
/// 2026-09-13 on Alexa.NET.Management 5.10.0), so every embedded-model PUT used
/// to downgrade catalog-backed types to the static seed until the next catalog
/// sync re-wired them.
/// </summary>
/// <param name="ArtistId">The artist catalog id, or null when the live model has none.</param>
/// <param name="ArtistVersion">The pinned artist catalog version, or null.</param>
/// <param name="AlbumId">The album catalog id, or null when the live model has none.</param>
/// <param name="AlbumVersion">The pinned album catalog version, or null.</param>
/// <param name="SeriesId">The series catalog id, or null when the live model has none.</param>
/// <param name="SeriesVersion">The pinned series catalog version, or null.</param>
internal sealed record CatalogWiring(
    string? ArtistId,
    string? ArtistVersion,
    string? AlbumId,
    string? AlbumVersion,
    string? SeriesId,
    string? SeriesVersion)
{
    public bool Any => ArtistId != null || AlbumId != null || SeriesId != null;
}

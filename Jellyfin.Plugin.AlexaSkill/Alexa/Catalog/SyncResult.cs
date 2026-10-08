using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Result of a catalog sync operation.
/// </summary>
public class SyncResult
{
    private readonly HashSet<CatalogType> _frozenTypes = new();

    /// <summary>
    /// Gets or sets a value indicating whether the sync completed successfully.
    /// JF-695 (applied by LibrarySyncService's gate, not enforced by this type):
    /// false also when <see cref="FrozenTypes"/> is non-empty, even if some
    /// locales completed, so a deterministically broken payload build never
    /// reads as a clean sync.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the sync operation.
    /// </summary>
    public DateTime SyncTime { get; set; }

    /// <summary>
    /// Gets or sets the number of artists synced.
    /// </summary>
    public int ArtistCount { get; set; }

    /// <summary>
    /// Gets or sets the number of albums synced.
    /// </summary>
    public int AlbumCount { get; set; }

    /// <summary>
    /// Gets or sets the number of TV series synced.
    /// </summary>
    public int SeriesCount { get; set; }

    /// <summary>
    /// Gets or sets the number of audiobooks synced (JF-823). The count lands
    /// through the audiobook wiring-table row's StoreCount element like the
    /// sibling types' counts.
    /// </summary>
    public int AudiobookCount { get; set; }

    /// <summary>
    /// Gets the catalog types (JF-695) whose payload build deterministically
    /// violated a construction invariant during the run. Per-type isolation in
    /// the sync froze each listed type while the sibling types synced normally:
    /// the frozen type minted no version, its catalog id was not forwarded to
    /// any locale's model injection, and its last-good pinned catalog reference
    /// stayed live. <see cref="Success"/> is a plain settable DTO property, so
    /// the "non-empty fails the run" rule is NOT enforced by this type: it is
    /// applied by LibrarySyncService's Success gate (the one production
    /// consumer), which must not stamp LastCatalogSync over a payload build
    /// that will fail identically on every run until the code drift is fixed.
    /// </summary>
    public IReadOnlyCollection<CatalogType> FrozenTypes => _frozenTypes;

    /// <summary>
    /// Records a frozen catalog type (internal: LibrarySyncService is the only writer).
    /// </summary>
    /// <param name="type">The catalog type whose payload build froze.</param>
    internal void RecordFrozenType(CatalogType type) => _frozenTypes.Add(type);
}

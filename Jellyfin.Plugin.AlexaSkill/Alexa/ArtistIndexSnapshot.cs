using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Immutable, atomically published state of <see cref="ArtistIndexService"/> (JF-432).
/// Publishing one reference replaces the previous sequential writes to separate volatile
/// fields, which ordered the individual assignments but not the group: a reader scheduled
/// between them saw a torn mix (new artist list against the old top-parent map, filtering
/// freshly added artists out). A reader that captures this snapshot sees either the complete
/// old state or the complete new state. The dictionaries are never mutated after publish,
/// so sharing them inside an immutable record is safe.
/// </summary>
/// <param name="Artists">All indexed artists.</param>
/// <param name="TopParentMap">Artist ID to top-level library folder ID.</param>
/// <param name="PhoneticCodes">Artist ID to pre-computed Double Metaphone codes.</param>
internal sealed record ArtistIndexSnapshot(
    IReadOnlyList<BaseItem> Artists,
    IReadOnlyDictionary<Guid, Guid> TopParentMap,
    IReadOnlyDictionary<Guid, (string Primary, string? Alternate)> PhoneticCodes)
{
    /// <summary>The pre-load state: read paths see empty data until the first publish.</summary>
    public static ArtistIndexSnapshot Empty { get; } = new(
        [],
        new Dictionary<Guid, Guid>(),
        new Dictionary<Guid, (string Primary, string? Alternate)>());
}

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Immutable, atomically published state of <see cref="ArtistIndexService"/> (JF-432).
/// Publishing one reference replaces the previous sequential writes to separate volatile
/// fields, which ordered the individual assignments but not the group: a reader scheduled
/// between them saw a torn mix (new artist list against the old top-parent map, filtering
/// freshly added artists out). A reader that captures this snapshot sees either the complete
/// old state or the complete new state.
/// <para>
/// Frozen at construction (JF-448, review F5): the artist array and the
/// <see cref="FrozenDictionary{K,V}"/>s are defensively copied from the loader's build
/// locals, so the loader cannot mutate a published snapshot afterwards. The arrays sit
/// behind IReadOnlyList but are technically castable back to T[]; the frozen maps are
/// not. Good enough for the invariant that matters (load-local detachment).
/// </para>
/// </summary>
public sealed record ArtistIndexSnapshot
{
    /// <summary>All indexed artists (frozen array).</summary>
    public IReadOnlyList<BaseItem> Artists { get; }

    /// <summary>Artist ID to top-level library folder ID.</summary>
    public IReadOnlyDictionary<Guid, Guid> TopParentMap { get; }

    /// <summary>Artist ID to pre-computed Double Metaphone codes.</summary>
    public IReadOnlyDictionary<Guid, (string Primary, string? Alternate)> PhoneticCodes { get; }

    /// <summary>
    /// Artist ID to the romanized (kana-to-romaji) form of the name, present ONLY
    /// for kana-containing names (JF-755 symmetric index-side normalization). The
    /// ORIGINAL name is never replaced or transliterated in place: it keeps flowing
    /// to every raw-name consumer (JF-690 exact-name resolution, speech, the
    /// JF-377/JF-420 string gates) while the always-romanized query side reaches
    /// the kana-named artist through this parallel key.
    /// </summary>
    public IReadOnlyDictionary<Guid, string> RomajiNames { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtistIndexSnapshot"/> class, freezing the
    /// loader's build locals into the published state. The inputs stay owned
    /// by the loader; every collection reachable from the snapshot is a copy.
    /// </summary>
    /// <param name="artists">All indexed artists, as loaded.</param>
    /// <param name="topParentMap">Artist ID to top-level library folder ID, as built.</param>
    /// <param name="phoneticCodes">Artist ID to Double Metaphone codes, as built.</param>
    /// <param name="romajiNames">Artist ID to the romanized name, kana-containing names only, as built.</param>
    internal ArtistIndexSnapshot(
        IEnumerable<BaseItem> artists,
        IEnumerable<KeyValuePair<Guid, Guid>> topParentMap,
        IEnumerable<KeyValuePair<Guid, (string Primary, string? Alternate)>> phoneticCodes,
        IEnumerable<KeyValuePair<Guid, string>> romajiNames)
    {
        Artists = artists.ToArray();
        TopParentMap = topParentMap.ToFrozenDictionary();
        PhoneticCodes = phoneticCodes.ToFrozenDictionary();
        RomajiNames = romajiNames.ToFrozenDictionary();
    }

    /// <summary>The pre-load state: read paths see empty data until the first publish.</summary>
    public static ArtistIndexSnapshot Empty { get; } = new([], [], [], []);
}

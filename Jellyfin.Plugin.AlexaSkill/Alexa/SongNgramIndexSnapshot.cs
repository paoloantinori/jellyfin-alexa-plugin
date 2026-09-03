using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Immutable, atomically published state of <see cref="SongNgramIndexService"/> (JF-432).
/// Publishing one reference replaces the previous sequential writes to separate volatile
/// fields, which ordered the individual assignments but not the group: a reader scheduled
/// between them saw a torn mix (old bigram candidate IDs against the new entries list,
/// returning empty results for an existing song). A reader that captures this snapshot sees
/// either the complete old state or the complete new state.
/// <para>
/// Frozen at construction (JF-448, review F5): the entries array and every
/// <see cref="FrozenDictionary{K,V}"/> (outer token maps AND their value lists) are
/// defensively copied from the loader's build locals, so the loader cannot mutate a
/// published snapshot afterwards. The arrays sit behind IReadOnlyList but are
/// technically castable back to T[]; the frozen maps are not.
/// </para>
/// </summary>
public sealed record SongNgramIndexSnapshot
{
    /// <summary>Bigram ("token token") to the songs whose title contains it (frozen).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<BaseItem>> BigramIndex { get; }

    /// <summary>Token to the songs whose title contains it (frozen).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<BaseItem>> SingleTokenIndex { get; }

    /// <summary>Double Metaphone code to the songs with a title token encoding to it (frozen).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<BaseItem>> PhoneticTokenIndex { get; }

    /// <summary>Song ID to top-level library folder ID (frozen).</summary>
    public IReadOnlyDictionary<Guid, Guid> TopParentMap { get; }

    /// <summary>Every indexed song (frozen array).</summary>
    public IReadOnlyList<BaseItem> AllEntries { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SongNgramIndexSnapshot"/> class, freezing the
    /// loader's build locals into the published state. The inputs stay owned
    /// by the loader; every collection reachable from the snapshot (outer dictionary and
    /// each per-token value list) is a copy.
    /// </summary>
    /// <param name="bigramIndex">Bigram to songs, as built.</param>
    /// <param name="singleTokenIndex">Token to songs, as built.</param>
    /// <param name="phoneticTokenIndex">Phonetic code to songs, as built.</param>
    /// <param name="topParentMap">Song ID to top-level library folder ID, as built.</param>
    /// <param name="allEntries">Every indexed song, as loaded.</param>
    internal SongNgramIndexSnapshot(
        IReadOnlyDictionary<string, List<BaseItem>> bigramIndex,
        IReadOnlyDictionary<string, List<BaseItem>> singleTokenIndex,
        IReadOnlyDictionary<string, List<BaseItem>> phoneticTokenIndex,
        IReadOnlyDictionary<Guid, Guid> topParentMap,
        IEnumerable<BaseItem> allEntries)
    {
        BigramIndex = Freeze(bigramIndex);
        SingleTokenIndex = Freeze(singleTokenIndex);
        PhoneticTokenIndex = Freeze(phoneticTokenIndex);
        TopParentMap = topParentMap.ToFrozenDictionary();
        AllEntries = allEntries.ToArray();
    }

    private static FrozenDictionary<string, IReadOnlyList<BaseItem>> Freeze(
        IReadOnlyDictionary<string, List<BaseItem>> index)
        => index.ToFrozenDictionary(pair => pair.Key, pair => (IReadOnlyList<BaseItem>)pair.Value.ToArray());

    /// <summary>The pre-load state: read paths see empty data until the first publish.</summary>
    public static SongNgramIndexSnapshot Empty { get; } = new(
        new Dictionary<string, List<BaseItem>>(),
        new Dictionary<string, List<BaseItem>>(),
        new Dictionary<string, List<BaseItem>>(),
        new Dictionary<Guid, Guid>(),
        []);
}

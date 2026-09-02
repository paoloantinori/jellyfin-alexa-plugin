using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Immutable, atomically published state of <see cref="SongNgramIndexService"/> (JF-432).
/// Publishing one reference replaces the previous sequential writes to separate volatile
/// fields, which ordered the individual assignments but not the group: a reader scheduled
/// between them saw a torn mix (old bigram candidate IDs against the new entries list,
/// returning empty results for an existing song). A reader that captures this snapshot sees
/// either the complete old state or the complete new state. The dictionaries and lists are
/// never mutated after publish, so sharing them inside an immutable record is safe.
/// </summary>
/// <param name="BigramIndex">Bigram ("token token") to the songs whose title contains it.</param>
/// <param name="SingleTokenIndex">Token to the songs whose title contains it.</param>
/// <param name="PhoneticTokenIndex">Double Metaphone code to the songs with a title token encoding to it.</param>
/// <param name="TopParentMap">Song ID to top-level library folder ID.</param>
/// <param name="AllEntries">Every indexed song.</param>
internal sealed record SongNgramIndexSnapshot(
    IReadOnlyDictionary<string, List<BaseItem>> BigramIndex,
    IReadOnlyDictionary<string, List<BaseItem>> SingleTokenIndex,
    IReadOnlyDictionary<string, List<BaseItem>> PhoneticTokenIndex,
    IReadOnlyDictionary<Guid, Guid> TopParentMap,
    IReadOnlyList<BaseItem> AllEntries)
{
    /// <summary>The pre-load state: read paths see empty data until the first publish.</summary>
    public static SongNgramIndexSnapshot Empty { get; } = new(
        new Dictionary<string, List<BaseItem>>(),
        new Dictionary<string, List<BaseItem>>(),
        new Dictionary<string, List<BaseItem>>(),
        new Dictionary<Guid, Guid>(),
        []);
}

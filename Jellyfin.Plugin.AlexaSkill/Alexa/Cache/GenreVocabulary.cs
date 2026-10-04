using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Cache;

/// <summary>
/// The built genre vocabulary the kana resolution tier matches against: the
/// genre-tag candidates deduplicated by NAME (Genre and MusicGenre items can both
/// exist for the same tag) and the Double Metaphone code set computed once per
/// tag name at scan time. Built and consumed by
/// <see cref="Util.SearchService.ResolveKanaGenreTagAsync"/>; carried by
/// <see cref="GenreVocabularyCache"/>.
/// </summary>
/// <param name="Candidates">The deduplicated genre-tag candidates.</param>
/// <param name="PhoneticCodes">The per-candidate Double Metaphone codes, keyed by candidate id.</param>
public sealed record GenreVocabulary(
    IReadOnlyList<BaseItem> Candidates,
    IReadOnlyDictionary<Guid, (string Primary, string? Alternate)> PhoneticCodes);

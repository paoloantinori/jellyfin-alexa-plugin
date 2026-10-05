using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Shared artist search with in-memory index preference and database fallback:
///   1. Name contains (in-memory) / SearchTerm (database)
///   2. Prefix first word + fuzzy
///   3. Prefix full query + fuzzy
///   4. Fuzzy match against all artists
/// Since JF-658 this is the ONE implementation: PlayArtistSongsIntentHandler's
/// former inline duplicate (the JF-382/JF-315 batch-6b plan) was folded in here
/// behind the caller policy axes documented on <see cref="SearchAsync"/>.
/// </summary>
internal static class ArtistSearch
{
    /// <summary>
    /// JF-426: the Italian nominative articles the it-IT model's article-carrying
    /// samples ("Suona i {musician}") can deliver RAW in the slot when the artist is
    /// not in Amazon's catalog (live probe 2026-09-13: "suona i 24 grana" arrived as
    /// musician='i 24 grana' while in-catalog artists get the article stripped
    /// Amazon-side). A leading article poisons every search tier (Contains/StartsWith
    /// all fail), so it is stripped HERE, at the single artist-entry choke point.
    /// Same six articles as KeywordMatcher.StopWords["it"] (the tokenizer twin; the
    /// it-IT YAML vocabulary comment cross-references this set).
    /// </summary>
    private static readonly string[] ItalianLeadingArticles = { "il", "lo", "la", "i", "gli", "le" };

    /// <summary>
    /// Strips ONE leading Italian nominative article from a raw musician slot value
    /// (only for it-IT requests; other locales never carry these articles). Returns
    /// the value unchanged when no article leads.
    /// </summary>
    /// <param name="musician">The raw slot value.</param>
    /// <param name="locale">The request locale.</param>
    /// <returns>The value without a leading article, or unchanged.</returns>
    internal static string StripLeadingArticle(string musician, string locale)
    {
        if (!string.Equals(locale, "it-IT", StringComparison.OrdinalIgnoreCase))
        {
            return musician;
        }

        string trimmed = musician.TrimStart();
        int space = trimmed.IndexOf(' ');
        if (space <= 0)
        {
            return musician;
        }

        string first = trimmed[..space];
        foreach (string article in ItalianLeadingArticles)
        {
            if (string.Equals(first, article, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(space + 1)..].TrimStart();
            }
        }

        return musician;
    }

    /// <summary>
    /// JF-381 tier-1 containment gate: a candidate name longer than the query by more
    /// than this many characters is a coincidental substring ("cup" in "Porcupine Tree"),
    /// not an intended match; the fuzzy/phonetic tiers handle the accent drift instead.
    /// Shared with the inline PlayArtistSongs search so the two paths cannot drift.
    /// </summary>
    internal const int Tier1ContainmentLengthBand = 10;

    /// <summary>
    /// JF-652 tie margin for kana-origin queries: when the winner and the runner-up
    /// BOTH reach the acceptance bar and their scores differ by at most this much,
    /// the acceptance is a silent coin flip between two real artists (the live
    /// 'クイーン' case: Queen and Keane both Double-Metaphone-collide at the 91
    /// floor, and the single-best pick resolved by iteration order). The outcome is
    /// the existing multi-artist disambiguation prompt at both kana decision points
    /// (PlayArtistSongsIntentHandler, CrossMediaFallback.TryEntityFallbackAsync),
    /// never an auto-play. A clear margin (strictly greater) auto-plays the winner.
    /// Shared by both decision points so they cannot drift.
    /// </summary>
    internal const int KanaOriginTieMargin = 5;

    /// <summary>
    /// JF-755 symmetric index-side normalization: the ONE query-side name resolver
    /// for the in-memory artist tiers. A kana-containing library name is indexed
    /// alongside its romanized form (<see cref="IArtistIndex.TryGetRomajiName"/>),
    /// and the query reaching these tiers is ALWAYS romanized (SearchAsync's entry
    /// romanization, JF-643), so the string-shaped legs must compare against the
    /// romaji key to see the kana-named artist at all. Lossless by construction for
    /// the Latin side: <c>KatakanaRomanizer.Romanize</c> passes every Latin run
    /// through unchanged, so a mixed name's romaji key contains everything Latin
    /// the original name did, while a pure-kana name contains no Latin to lose.
    /// The ORIGINAL name keeps serving every raw-name consumer (JF-690 exact-name
    /// resolution, speech, the JF-377/JF-420 string gates): this resolver only
    /// ADDS the parallel key. A null index (cold-window DB legs, test fakes without
    /// the map) degrades to the raw name, the pre-JF-755 behavior.
    /// </summary>
    /// <param name="index">The pinned index view when one exists; null degrades to the raw name.</param>
    /// <param name="artist">The candidate artist.</param>
    /// <returns>The romaji key when the index has one for this artist, else the raw name.</returns>
    internal static string QueryNameFor(IArtistIndex? index, BaseItem artist)
        => index != null && index.TryGetRomajiName(artist.Id, out string? romaji) ? romaji : artist.Name ?? string.Empty;

    /// <summary>
    /// JF-659: whether a musician query is kana-origin, the ONE definition of the
    /// origin flag the JF-652 acceptance bar consumes (PlayArtistSongs, the JF-471
    /// album-by-artist gate, CrossMediaFallback). A query carrying an ER canonical
    /// keeps the flag false WHATEVER the canonical's script: ER resolution, not
    /// Latinity, is what makes the bar inert (the ER match IS the collision
    /// evidence the bar demands). Since JF-658 every musician handler feeds the
    /// canonical through <see cref="SearchAsync"/>, whose entry romanization is
    /// the ONE query-side choke point. JF-755 makes that sound for kana canonicals
    /// too: the romanized canonical now exact-hits the kana-tagged artist's
    /// indexed romaji key at tier 1, so the ER evidence resolves to the artist it
    /// points at instead of falling to the tier-4 Latin 91-tie the bar cannot see
    /// (the JF-658 finding). Accepted residual: with a STALE catalog (canonical
    /// resolved but the kana artist deleted) the romanized query fuzzy-recovers a
    /// Latin artist with the bar inert, the same recovery a Latin canonical whose
    /// artist is absent already performs, not a new class. The canonical is
    /// slot-layer knowledge, so it arrives here as a plain string rather than
    /// inside <see cref="PassesKanaOriginAcceptance(string, BaseItem, int, int, IArtistIndex?)"/>,
    /// which stays slot-agnostic by design.
    /// </summary>
    /// <param name="canonical">The ER canonical when the slot resolved, else null.</param>
    /// <param name="raw">The raw (pre-romanization) slot value.</param>
    /// <returns>True when no canonical resolved and the raw value contains kana.</returns>
    internal static bool IsKanaOriginQuery(string? canonical, string? raw)
        => canonical == null && raw is not null && KatakanaRomanizer.ContainsKana(raw);

    /// <summary>
    /// JF-652 kana-acceptance bar, the ONE definition shared by every kana decision
    /// point (PlayArtistSongs, TryEntityFallbackAsync, the JF-471 album-by-artist
    /// gate): the user's threshold on the score AND a REAL Double Metaphone code
    /// collision between the (already-romanized) query and the candidate. The
    /// collision is checked on the CODES, never inferred from the score band: plain
    /// PartialRatio reaches 91-99 for near-identical strings with no code collision,
    /// so a score alone cannot carry collision provenance (review round, F1).
    /// </summary>
    /// <param name="query">The romanized query.</param>
    /// <param name="candidate">The candidate artist.</param>
    /// <param name="score">The candidate's matcher score.</param>
    /// <param name="threshold">The user's fuzzy threshold.</param>
    /// <param name="index">The pinned artist index when one exists; its pre-computed
    /// codes are used, otherwise the candidate name is encoded here.</param>
    /// <returns>True when both the threshold and the real collision hold.</returns>
    internal static bool PassesKanaOriginAcceptance(
        string query,
        BaseItem candidate,
        int score,
        int threshold,
        IArtistIndex? index)
        => PassesKanaOriginAcceptance(DoubleMetaphone.Encode(query), candidate, score, threshold, index);

    /// <summary>
    /// Codes-carried form of <see cref="PassesKanaOriginAcceptance(string, BaseItem, int, int, IArtistIndex?)"/>
    /// for callers that encode the query once and reuse it across several candidate
    /// checks (the FuzzyMatcher encode-once shape; review round 2, finding 4).
    /// </summary>
    internal static bool PassesKanaOriginAcceptance(
        (string Primary, string? Alternate) queryCodes,
        BaseItem candidate,
        int score,
        int threshold,
        IArtistIndex? index)
        => score >= threshold && PassesKanaOriginCollision(queryCodes, candidate, index);

    /// <summary>
    /// Whether the (already-romanized) query and the candidate REALLY collide on
    /// Double Metaphone codes: the candidate's codes come from the pinned index's
    /// pre-computed table when available, otherwise the candidate name is encoded
    /// at the decision point (so the check is uniform on every path, cold index
    /// included; review round, F1).
    /// </summary>
    internal static bool PassesKanaOriginCollision(string query, BaseItem candidate, IArtistIndex? index)
        => PassesKanaOriginCollision(DoubleMetaphone.Encode(query), candidate, index);

    /// <summary>
    /// Codes-carried form of <see cref="PassesKanaOriginCollision(string, BaseItem, IArtistIndex?)"/>.
    /// </summary>
    internal static bool PassesKanaOriginCollision(
        (string Primary, string? Alternate) queryCodes,
        BaseItem candidate,
        IArtistIndex? index)
    {
        (string Primary, string? Alternate) candidateCodes =
            index != null && index.TryGetPhoneticCode(candidate.Id, out var codes)
                ? codes
                : DoubleMetaphone.Encode(candidate.Name ?? string.Empty);
        return FuzzyMatcher.PhoneticCodesMatch(
            queryCodes.Primary, queryCodes.Alternate, candidateCodes.Primary, candidateCodes.Alternate);
    }

    /// <summary>
    /// The pinned-index scoring ternary, one definition (the JF-382 no-third-copy
    /// rule): the phonetic overload when the pinned index exists, the plain overload
    /// otherwise. Review round, F2c. JF-755: the phonetic branch scores through
    /// <see cref="QueryNameFor"/> so a kana-named artist's romaji key (not its
    /// raw-kana name, which the always-romanized query scores 0 against) reaches
    /// the matcher, exact-hitting the romanized canonical instead of riding the
    /// 91-floor tie the bar's near-tie margin then has to arbitrate.
    /// </summary>
    internal static (BaseItem Item, int Score)? ScoreBestWithCodes(
        string query, IReadOnlyList<BaseItem> candidates, IArtistIndex? pinnedIndex)
        => pinnedIndex != null
            ? FuzzyMatcher.FindBestMatchWithScore(
                query,
                candidates,
                a => QueryNameFor(pinnedIndex, a),
                a => a.Id,
                id => pinnedIndex.TryGetPhoneticCode(id, out var codes) ? codes : null)
            : FuzzyMatcher.FindBestMatchWithScore(query, candidates, a => a.Name);

    /// <summary>
    /// JF-652 near-tie runner-up search, ONE shared implementation for both kana
    /// decision points (review round, F2a): scores every rival of the winner, and
    /// returns the near-tied PAIR only when the best rival clears the FULL kana
    /// acceptance bar (threshold AND a real code collision) AND sits within
    /// <see cref="KanaOriginTieMargin"/> of the winner. Null means a clear margin
    /// (or no rival at all) and the winner auto-plays. The pair is ordered by score
    /// DESCENDING (review round 2, finding 1): a rival that OUTSCORES the winner
    /// passes the margin test trivially, and the ask sites present First first, so
    /// an unordered pair would make "yes" play the lower-scoring artist. Pool
    /// sourcing stays caller-side: the in-memory path has the full index list, the
    /// cross-media path re-scopes the pinned index, and a cold-window path without
    /// a pool skips tie detection entirely. The query codes are encoded ONCE and
    /// reused for both bar checks (review round 2, finding 4).
    /// </summary>
    /// <param name="query">The romanized query.</param>
    /// <param name="winner">The accepted winner.</param>
    /// <param name="winnerScore">The winner's acceptance score.</param>
    /// <param name="pool">The candidate pool to scan for rivals (winner excluded here).</param>
    /// <param name="pinnedIndex">The pinned index view (may be null; codes are then encoded per candidate).</param>
    /// <param name="threshold">The user's fuzzy threshold.</param>
    /// <returns>The near-tied pair ordered by score descending, or null.</returns>
    internal static (BaseItem First, int FirstScore, BaseItem Second, int SecondScore)? FindNearTiedRunnerUp(
        string query,
        BaseItem winner,
        int winnerScore,
        IReadOnlyList<BaseItem> pool,
        IArtistIndex? pinnedIndex,
        int threshold)
    {
        var queryCodes = DoubleMetaphone.Encode(query);
        var rivals = pool.Where(a => !a.Id.Equals(winner.Id)).ToList();
        if (rivals.Count == 0)
        {
            return null;
        }

        var runnerUp = ScoreBestWithCodes(query, rivals, pinnedIndex);
        if (runnerUp == null
            || winnerScore - runnerUp.Value.Score > KanaOriginTieMargin
            || !PassesKanaOriginAcceptance(queryCodes, runnerUp.Value.Item, runnerUp.Value.Score, threshold, pinnedIndex))
        {
            return null;
        }

        return runnerUp.Value.Score > winnerScore
            ? (runnerUp.Value.Item, runnerUp.Value.Score, winner, winnerScore)
            : (winner, winnerScore, runnerUp.Value.Item, runnerUp.Value.Score);
    }

    /// <summary>
    /// Whether a candidate name is within the JF-381 containment band for the query.
    /// Applied to EVERY containment-shaped candidate source (in-memory tier-1 filter,
    /// database SearchTerm results, database NameContains results) in both search
    /// implementations, so a short query inside a long name can never short-circuit the
    /// tier chain before the phonetic tier runs.
    /// </summary>
    /// <param name="candidateName">The candidate's name.</param>
    /// <param name="query">The raw query.</param>
    /// <returns>True when the candidate may be a genuine containment match.</returns>
    internal static bool PassesContainmentBand(string? candidateName, string query)
        => !string.IsNullOrEmpty(candidateName) && candidateName.Length <= query.Length + Tier1ContainmentLengthBand;

    /// <summary>
    /// JF-420.1: exact-name equality between a query and a candidate name
    /// (case-insensitive, end-trimmed; the same normalization as FuzzyMatcher's
    /// exact-match concept). The JF-420 gate exists to resolve CONTAINMENT matches
    /// (artist name inside a LONGER query); equality is the degenerate case where
    /// the containment exemption inflates both sides to ContainmentScore, the margin
    /// is always 0, and an exact request is demoted to a disambiguation prompt (live:
    /// "Soul Coughing" with "Soul Coughing &amp; Roni Size" in the library). An exact
    /// match is the strongest possible signal: it must auto-play. NOT accent-insensitive
    /// by design: an accented query ("måneskin" vs "Måneskin") is ASR accent drift,
    /// which the phonetic tiers exist to resolve. JF-690 also uses it as the
    /// resolution predicate for ER canonical names (a catalog value IS the library
    /// artist name, so only the exact hit counts; fuzzy expansion would re-introduce
    /// the arbitrary arbitration the gate exists to remove), which is why it lives
    /// here and not on the handler.
    /// </summary>
    /// <param name="query">The query (or ER canonical name).</param>
    /// <param name="name">The candidate library artist name.</param>
    /// <returns>True when the two are equal ignoring case after trimming. Null or
    /// empty on either side reads as false (the pool walk feeds every published
    /// index entry through here; a blank-named entry can never exact-match, the
    /// PassesContainmentBand guard convention).</returns>
    internal static bool IsExactNameMatch(string? query, string? name)
        => !string.IsNullOrEmpty(query) && !string.IsNullOrEmpty(name)
            && string.Equals(query.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// JF-690: resolve ER candidate names to library artists by exact-name equality
    /// (<see cref="IsExactNameMatch"/>) over the user-scoped pool. One artist per
    /// name (first pool hit, deterministic in pool order), de-duplicated by id (a
    /// deliberate second layer behind the caller's name de-dup: a duplicated
    /// candidate can never manufacture a two-entry prompt of one artist, whoever
    /// calls this), and the RESULT preserves the candidates' order: callers feed
    /// the ER values list, whose order is Amazon's likelihood rank, so the first
    /// resolved artist is the one a plain "yes" plays (AskMultipleArtists'
    /// winner-first contract).
    /// </summary>
    /// <param name="names">The candidate names (ER canonical values, rank order).</param>
    /// <param name="pool">The user-scoped artist pool (a pinned index view).</param>
    /// <returns>The distinct resolved artists in candidate order; empty when none resolve.</returns>
    internal static List<BaseItem> ResolveExactNameMatches(IReadOnlyList<string> names, IReadOnlyList<BaseItem> pool)
    {
        List<BaseItem> resolved = new();
        HashSet<Guid> seen = new();
        foreach (string name in names)
        {
            BaseItem? match = pool.FirstOrDefault(candidate => IsExactNameMatch(name, candidate.Name));
            if (match != null && seen.Add(match.Id))
            {
                resolved.Add(match);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Whether a tier-2 prefix match covers only the first word of a multi-word query,
    /// leaving the rest of the query's content completely unmatched (JF-417). This shape
    /// must NOT short-circuit the tier chain: the tier-4 fuzzy-all pass often has a much
    /// better full-name match ("P!nk floyd" -> "Pink Floyd" at ~85, while "P!nk" only
    /// covers the first word). The guard is deliberately narrow: it fires only when the
    /// candidate is essentially JUST the first word (within a small margin), the first
    /// word is less than half the query, and the query is multi-word. Single-word queries
    /// (the ASR-truncation shape "crash" -> "Crash Test Dummies") and full-coverage
    /// candidates ("the beatles" -> "The Beatles") are unaffected.
    /// </summary>
    /// <param name="query">The full raw query.</param>
    /// <param name="firstWord">The first word extracted from the query (tier-2 prefix).</param>
    /// <param name="candidateName">The tier-2 matched candidate's name.</param>
    /// <returns>True when the match is a partial first-word shape and higher tiers should run.</returns>
    internal static bool IsPartialFirstWordMatch(string query, string firstWord, string? candidateName)
    {
        if (string.IsNullOrEmpty(candidateName) || !query.Contains(' '))
        {
            return false;
        }

        // The candidate is essentially just the first word (small margin for
        // punctuation or slight variations). If it extends meaningfully beyond the
        // first word (like "The Beatles" for query "the beatles"), it covers the
        // query's content and the guard must not fire.
        bool candidateIsJustFirstWord = candidateName.Length <= firstWord.Length + 2;

        // The first word is less than half the query, meaning the majority of the
        // query's content ("floyd" in "P!nk floyd") is completely uncovered.
        bool firstWordIsMinority = firstWord.Length < query.Length * 0.5;

        return candidateIsJustFirstWord && firstWordIsMinority;
    }

    /// <summary>
    /// JF-437 word-coverage tier: candidates whose name WORD-SET (tokenized, articles
    /// and stop words stripped by <see cref="KeywordMatcher.Tokenize"/>, which always
    /// strips English stop words and strips the locale's since JF-389) is a subset of
    /// the query's word-set. This is the tier-1 containment shape without the
    /// CONTIGUITY requirement: a trailing qualifier breaks the substring ('beatles
    /// live' contains no 'the beatles') but not the word coverage, and tier-4's
    /// partial window then ranks a near-anagram short name above the intended artist
    /// ('Eagles' 83 vs 'The Beatles' 27, live finding 2026-09-01).
    ///
    /// Selection (review round): (1) the FULLEST distinct-word coverage wins
    /// ('Miles Davis' over 'Miles'); (2) among count ties, candidates whose name
    /// tokens appear in the query IN ORDER as a contiguous token subsequence are
    /// preferred ('Miles Davis' over the re-tagged variant 'Davis Miles'); (3) there
    /// is deliberately NO first-word winner-take-all: a carrier-word-named artist
    /// ('The Band' for the carrier-bleed query 'la band radiohead') and the real
    /// artist tie, and honest ties are returned TOGETHER so the caller's
    /// disambiguation prompt resolves them instead of a silent wrong play.
    ///
    /// Known limits (documented, by design at this tier): a single character of ASR
    /// drift defeats the byte-exact word membership ('beattles live' still falls to
    /// tier 4, where the phonetic tiers own drift); single-token queries early-return
    /// (tier-1 Contains already covers every subset they could match); the DB search
    /// paths have no equivalent tier (cold-window divergence, same trade-off class as
    /// JF-381/JF-417).
    /// </summary>
    /// <param name="query">The raw musician query.</param>
    /// <param name="pool">All candidate artists (the in-memory index list).</param>
    /// <param name="locale">The request locale, for stop-word stripping.</param>
    /// <param name="index">The pinned index view when the caller has one; its
    /// JF-755 romaji key (when present) tokenizes IN PLACE of the kana name, since
    /// the query tokens reaching this tier are always romanized and the subset
    /// check must hold for ONE coherent reading of the name (a union of both
    /// readings would demand the query cover the kana tokens too, breaking the
    /// subset for every kana-named artist). Deliberately REQUIRED (JF-755 altitude
    /// round): the silent-default shape let the CrossMediaFallback valve below
    /// omit it and quietly keep the pre-JF-755 kana degradation; callers without
    /// a view pass null explicitly.</param>
    /// <returns>The best word-coverage candidates (possibly several on a tie), or an empty list.</returns>
    internal static List<BaseItem> WordCoverageCandidates(string query, IEnumerable<BaseItem> pool, string locale, IArtistIndex? index)
    {
        string[] queryTokens = KeywordMatcher.Tokenize(query, locale);
        if (queryTokens.Length < 2)
        {
            // Single-token queries: tier-1 Contains already matches every name they
            // could subset-match, so the pool scan is pure cost (review round).
            return new List<BaseItem>();
        }

        var queryWords = new HashSet<string>(queryTokens, StringComparer.OrdinalIgnoreCase);

        List<(BaseItem Artist, int WordCount, bool InOrder)> matches = new();
        foreach (BaseItem candidate in pool)
        {
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                continue;
            }

            string[] nameWords = KeywordMatcher.Tokenize(QueryNameFor(index, candidate), locale);
            if (nameWords.Length == 0 || !nameWords.All(queryWords.Contains))
            {
                continue;
            }

            // Distinct count: the query side is a set, so a repeated word ("Boom
            // Boom") covers one word and must not outrank a two-distinct-word name.
            var covered = new HashSet<string>(nameWords, StringComparer.OrdinalIgnoreCase);

            // Contiguous in-order subsequence of the query tokens ('miles davis' in
            // 'miles davis live'): the name reads as the query's leading phrase.
            bool inOrder = ContainsTokenSubsequence(queryTokens, nameWords);
            matches.Add((candidate, covered.Count, inOrder));
        }

        if (matches.Count == 0)
        {
            return new List<BaseItem>();
        }

        int bestCount = matches.Max(m => m.WordCount);
        var bestByCount = matches.Where(m => m.WordCount == bestCount).ToList();
        bool anyInOrder = bestByCount.Any(m => m.InOrder);
        return bestByCount
            .Where(m => m.InOrder == anyInOrder)
            .Select(m => m.Artist)
            .ToList();
    }

    /// <summary>Whether <paramref name="nameWords"/> appears as a contiguous
    /// subsequence inside <paramref name="queryTokens"/> (case-insensitive).</summary>
    private static bool ContainsTokenSubsequence(string[] queryTokens, string[] nameWords)
    {
        for (int start = 0; start + nameWords.Length <= queryTokens.Length; start++)
        {
            bool all = true;
            for (int i = 0; i < nameWords.Length; i++)
            {
                if (!string.Equals(queryTokens[start + i], nameWords[i], StringComparison.OrdinalIgnoreCase))
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// JF-457 bound on the album-scope post-filter: at most this many artists of one
    /// bypass-tier result set are verified, one Limit=1 MusicAlbum query each. Entries
    /// past the cap are DROPPED, never returned unverified. The bound prices the
    /// SELECTION pool, not just the spoken window: the downstream re-rankers pick the
    /// winner from this returned list (the artist prompt itself speaks Take(3)), and
    /// with more than 8 band-passing tier-1 rows a non-empty kept list short-circuits
    /// the chain before tiers 2-4 could recover a true match sitting past position 8
    /// of the unordered result. Accepted trade (review 2026-09-04): the reachability
    /// is narrow (restricted user AND cold/disabled index AND >8 rows AND the true
    /// match in the tail), the dropped direction is privacy-correct, and the warm
    /// index serves the full scope.
    /// </summary>
    internal const int MaxAlbumScopeChecks = 8;

    /// <summary>
    /// JF-457 album-scope post-filter for the items-by-name bypass DB tiers. The bypass
    /// (LibraryFilter.ApplyItemsByNameBypass) exists so a library-restricted user's
    /// cold-window DB artist query can still find FOLDERLESS artists (TopParentId NULL,
    /// the metadata-path majority); its cost is that Jellyfin matches every MusicArtist
    /// row regardless of library, so an excluded library's artist NAME could otherwise
    /// surface in a not-found or disambiguation prompt (names only: songs queries keep
    /// the strict TopParentIds filter, so no playable content ever leaks). This filter
    /// bounds the leak: an artist survives only when at least one album by them (or
    /// containing a track by them, the same net ArtistIndexService scopes folderless
    /// artists with) lives under the user's resolved top parents.
    /// <para>
    /// Cost bounds: skipped entirely for unrestricted users (null scope) and for empty
    /// result sets; at most <see cref="MaxAlbumScopeChecks"/> Limit=1 MusicAlbum queries
    /// per tier result set. Deliberately assigns TopParentIds RAW (not via
    /// ApplyLibraryFilter): MusicAlbum is not an items-by-name type, so the bypass must
    /// not fire on the verification query itself.
    /// </para>
    /// </summary>
    /// <param name="artists">The bypass-tier artist results (names not yet spoken).</param>
    /// <param name="topParentIds">The user's resolved library scope, or null when unrestricted.</param>
    /// <param name="dbQuery">The caller's database query channel (RetryAsync-wrapped GetItemList).</param>
    /// <param name="logger">The caller's logger.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Only the artists whose album scope intersects the user's libraries.</returns>
    internal static async Task<IReadOnlyList<BaseItem>> FilterByAlbumScopeAsync(
        IReadOnlyList<BaseItem> artists,
        Guid[]? topParentIds,
        Func<InternalItemsQuery, CancellationToken, Task<IReadOnlyList<BaseItem>>> dbQuery,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Unrestricted scope (the bypass is inert without TopParentIds anyway) or
        // nothing to verify: zero cost.
        if (topParentIds is not { Length: > 0 } || artists.Count == 0)
        {
            return artists;
        }

        var kept = new List<BaseItem>(Math.Min(artists.Count, MaxAlbumScopeChecks));
        foreach (BaseItem artist in artists.Take(MaxAlbumScopeChecks))
        {
            var albumQuery = new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
                ArtistIds = new[] { artist.Id },
                TopParentIds = topParentIds,
                Limit = 1,
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false, AddCurrentProgram = false }
            };

            IReadOnlyList<BaseItem> albums = await dbQuery(albumQuery, cancellationToken).ConfigureAwait(false);
            if (albums.Count > 0)
            {
                kept.Add(artist);
            }
            else
            {
                logger.LogInformation(
                    "ArtistSearch: album-scope post-filter dropped bypass-tier artist '{Artist}' (no album under the user's libraries, JF-457)",
                    artist.Name);
            }
        }

        return kept;
    }

    /// <summary>
    /// Tier 1.5 entry point shared by BOTH search implementations (inline
    /// PlayArtistSongs chain and <see cref="SearchAsync"/>'s in-memory branch), so
    /// the stopwatch and the tier log line exist once. Runs AFTER tiers 2-3 and
    /// BEFORE tier 4: earlier placement short-circuits the tier-2 fuzzy/phonetic
    /// resolution of ASR drift ('soul coughin' -> 'Soul' would preempt 'Soul
    /// Coughing', review round probe); later placement lets tier 4's partial window
    /// commit the near-anagram wrong match this tier exists to prevent.
    /// </summary>
    /// <param name="query">The raw musician query.</param>
    /// <param name="pool">All candidate artists.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="logger">The caller's logger.</param>
    /// <param name="candidates">The selected word-coverage candidates when true.</param>
    /// <param name="index">The pinned index view for the JF-755 romaji keys; null keeps raw names.</param>
    /// <returns>True when the tier produced candidates.</returns>
    internal static bool TryWordCoverageTier(
        string query,
        IEnumerable<BaseItem> pool,
        string locale,
        ILogger logger,
        out List<BaseItem> candidates,
        IArtistIndex? index)
    {
        var sw = Stopwatch.StartNew();
        candidates = WordCoverageCandidates(query, pool, locale, index);
        sw.Stop();
        if (candidates.Count > 0)
        {
            logger.LogInformation(
                "ArtistSearch: tier=1.5 duration={TierMs}ms results={Count} method=WordCoverage query='{Query}'",
                sw.ElapsedMilliseconds, candidates.Count, query);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The one 4-tier artist search (JF-658: the PlayArtistSongs inline duplicate
    /// was folded in here). <paramref name="mode"/>, <paramref name="asrCompoundWordFixEnabled"/>,
    /// and <paramref name="parallelDbTiers"/> are CALLER POLICY axes, not judgments:
    /// they select recall/latency behavior inside the recall layer and never decide
    /// whether a returned candidate should play (those decisions stay at the
    /// handlers' decision points, the JF-408 rule this class documents in its doc).
    /// The defaults preserve the pre-JF-658 shared-caller behavior (Thorough,
    /// sequential DB tiers, no ASR variants: the flag was the inline chain's
    /// config read, which the caller now passes explicitly).
    /// <para>
    /// Fast mode's documented exceptions (CLAUDE.md, Search Response Mode) live
    /// at the branch sites below: the in-memory path skips the prefix and
    /// word-coverage tiers (tier 1 then fuzzy-all); the DB path issues a single
    /// ungated SearchTerm query with no ASR variants and no fallback tiers (the
    /// ungated-band rationale sits on the tier-1 gate).
    /// </para>
    /// </summary>
    /// <param name="musician">The musician query (romanized at entry, JF-643).</param>
    /// <param name="user">The plugin user (thresholds, library scope).</param>
    /// <param name="libraryManager">The library manager (scope resolution).</param>
    /// <param name="artistIndex">The artist index; a caller-pinned view is honored (capture is idempotent).</param>
    /// <param name="logger">The caller's logger.</param>
    /// <param name="dbQuery">The caller's database query channel (retry-wrapped).</param>
    /// <param name="locale">The request locale (stop-word stripping in the gates).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <param name="mode">Fast skips recall tiers for speed; Thorough (default) runs the full chain.</param>
    /// <param name="asrCompoundWordFixEnabled">Whether the DB tier 1 retries ASR compound-word variants (Thorough only).</param>
    /// <param name="parallelDbTiers">Whether the Thorough DB tiers 2-4 run as Task.WhenAll with the 2&gt;3&gt;4 priority pick (the inline chain's structure kept verbatim per the JF-315 6b plan: the tier tasks' retry backoffs overlap, while the synchronous queries themselves share the request thread) instead of sequentially.</param>
    /// <param name="preloadedPool">JF-715: an already-materialized scoped pool
    /// the in-memory branch consumes instead of its own GetArtists fetch (the
    /// fall-through legs of the multi-value-ER gate, whose TryArbitrate already
    /// materialized the identical pool; without the threading the rarest leg
    /// paid one redundant ResolveForUser plus a full artist-list copy).
    /// CONTRACT: the pool must be
    /// <c>artistIndex.Pin().GetArtists(LibraryFilter.ResolveForUser(user, libraryManager, logger))</c>
    /// for the SAME <paramref name="artistIndex"/> instance passed here (exactly
    /// what the multi-value-ER arbitration ships on its result), so the artist
    /// list and the phonetic codes resolve from ONE publish (the JF-448
    /// invariant; a pool from a different publish breaks it). Null (the
    /// default) keeps the internal fetch; the database branch never consumes a
    /// pool.</param>
    /// <returns>The matched artist candidates (possibly several; judgment is the caller's).</returns>
    public static async Task<IReadOnlyList<BaseItem>> SearchAsync(
        string musician,
        Entities.User? user,
        ILibraryManager libraryManager,
        IArtistIndex? artistIndex,
        ILogger logger,
        Func<InternalItemsQuery, CancellationToken, Task<IReadOnlyList<BaseItem>>> dbQuery,
        string locale,
        CancellationToken cancellationToken,
        SearchResponseMode mode = SearchResponseMode.Thorough,
        bool asrCompoundWordFixEnabled = false,
        bool parallelDbTiers = false,
        IReadOnlyList<BaseItem>? preloadedPool = null)
    {
        // JF-643: the ONE query-side romanization for the artist chain (both the
        // in-memory tiers and every database tier below search Latin library names;
        // Japanese ASR delivers the musician slot as katakana). Idempotent for
        // already-Latin queries (same instance back), so callers that romanized
        // earlier add no cost.
        musician = KatakanaRomanizer.Romanize(musician);

        // JF-419.2 choke point: see IndexWarmingGate (layer 2 of the warming gate)
        IndexWarmingGate.EnsureReady(artistIndex);

        // JF-448 (review F2): pin ONE snapshot for the WHOLE chain. Previously
        // GetArtists captured internally while every phonetic-code lookup in the
        // fuzzy tiers re-read the LIVE field: a publish landing in that 1-10ms gap
        // served the artist list of snapshot A against the phonetic codes of
        // snapshot B, nulling the code lookup, skipping the JF-381 phonetic floor,
        // and playing the wrong artist for one request. The pinned view resolves
        // BOTH from one publish; capture is idempotent, so a caller that already
        // pinned (TryEntityFallbackAsync) adds no extra hop. Pin degrades to live
        // reads (the pre-JF-448 behavior) when the implementation cannot pin.
        IArtistIndex? pinned = artistIndex.Pin();

        var totalSw = Stopwatch.StartNew();
        var tierSw = Stopwatch.StartNew();
        int tierReached = 0;
        string searchSource = "Database";

        IReadOnlyList<BaseItem> artists;

        // Resolve the library scope ONCE for both branches (E4 hoist): the in-memory
        // read and every database tier below consume the same value, so no tier
        // re-resolves it (ResolveForUser is cached, but one call is still cheaper).
        // JF-715: a preloaded pool the in-memory branch is about to consume makes
        // the resolution dead on that branch (its only in-memory consumer is the
        // fetch the pool replaces; the database tiers live in the other branch),
        // so the pool-covered leg skips it (simplify/efficiency round).
        Guid[]? topParentIds = preloadedPool != null && pinned?.IsReady == true
            ? null
            : LibraryFilter.ResolveForUser(user, libraryManager, logger);

        if (pinned?.IsReady == true)
        {
            searchSource = "InMemory";
            // JF-715: a caller-supplied pool (the multi-value-ER gate's fetch)
            // replaces the internal materialization; the contract on the
            // parameter guarantees it is this same view's scoped list, so the
            // tiers below read list and phonetic codes from ONE publish. The
            // identity pair in the log converts a contract breach (a pool from
            // another view or scope) from a silent JF-448/JF-457 hazard into a
            // diagnosable one: triage compares viewId against the gate's own
            // fetch log line for the same request (gate-marker rework F3).
            if (preloadedPool != null)
            {
                logger.LogDebug(
                    "ArtistSearch: consuming preloaded pool ({Count} artists, viewId={ViewId}, poolId={PoolId}, JF-715)",
                    preloadedPool.Count,
                    RuntimeHelpers.GetHashCode(pinned),
                    RuntimeHelpers.GetHashCode(preloadedPool));
            }

            var allArtists = preloadedPool ?? pinned.GetArtists(topParentIds);

            // Tier 1: name contains query, with the JF-381 coincidental-containment gate.
            // Without the gate a short query inside a long name wins tier 1 and stops the
            // chain (live 2026-08-28 21:17: ASR "cup" for "Koop" returned "Porcupine Tree"
            // here and the album path played the wrong artist, while the inline
            // PlayArtistSongs search, which HAS the gate, correctly fell through to the
            // phonetic tier). Gating lets tiers 2-4 resolve the accent drift instead.
            // JF-755: the operand is the query-side name (romaji key when present), so a
            // kana-tagged artist is contained-matched through its indexed romaji form.
            tierSw.Restart();
            artists = allArtists
                .Where(a =>
                {
                    string name = QueryNameFor(pinned, a);
                    return name.Contains(musician, StringComparison.OrdinalIgnoreCase)
                        && PassesContainmentBand(name, musician);
                })
                .ToList();
            tierSw.Stop();
            tierReached = 1;
            logger.LogInformation(
                "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=InMemoryContains query='{Query}'",
                tierSw.ElapsedMilliseconds, artists.Count, musician);

            if (artists.Count == 0)
            {
                if (mode == SearchResponseMode.Fast)
                {
                    // Fast mode: skip the prefix tiers and the word-coverage tier;
                    // tier 1 falls straight to fuzzy-all (speed over recall by
                    // design; the recall machinery is Thorough-only, CLAUDE.md's
                    // Search Response Mode).
                    tierSw.Restart();
                    BaseItem? fuzzy = FuzzyMatch(musician, allArtists, user, pinned);
                    tierSw.Stop();
                    tierReached = 4;
                    logger.LogInformation(
                        "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=InMemoryFuzzyAll query='{Query}' mode=Fast",
                        tierSw.ElapsedMilliseconds, fuzzy != null, musician);
                    if (fuzzy != null)
                    {
                        artists = new List<BaseItem> { fuzzy };
                    }
                }
                else
                {
                    // Tier 2: prefix first word + fuzzy
                    string firstWord = FirstWordOf(musician);
                    BaseItem? deferredTier2Match = null;
                    tierSw.Restart();
                    var prefixCandidates = allArtists
                        .Where(a => QueryNameFor(pinned, a).StartsWith(firstWord, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    BaseItem? fuzzy = FuzzyMatch(musician, prefixCandidates, user, pinned);
                    tierSw.Stop();
                    tierReached = 2;
                    logger.LogInformation(
                        "ArtistSearch: tier=2 duration={TierMs}ms matched={Matched} method=InMemoryPrefixFirstWord query='{Query}' prefix='{Prefix}'",
                        tierSw.ElapsedMilliseconds, fuzzy != null, musician, firstWord);
                    if (fuzzy != null)
                    {
                        if (IsPartialFirstWordMatch(musician, firstWord, fuzzy.Name))
                        {
                            // JF-417: the tier-2 candidate covers only the first word of a
                            // multi-word query ("P!nk" for "P!nk floyd"). Defer acceptance;
                            // tier-4 fuzzy-all may have a better full-name match ("Pink Floyd").
                            deferredTier2Match = fuzzy;
                            logger.LogInformation(
                                "ArtistSearch: tier=2 deferred (partial first-word match: candidate='{Candidate}' covers only '{FirstWord}' of query '{Query}', JF-417)",
                                fuzzy.Name, firstWord, musician);
                        }
                        else
                        {
                            artists = new List<BaseItem> { fuzzy };
                        }
                    }

                    // Tier 3: prefix full query + fuzzy
                    if (artists.Count == 0 && !string.Equals(firstWord, musician, StringComparison.Ordinal))
                    {
                        tierSw.Restart();
                        var fullPrefixCandidates = allArtists
                            .Where(a => QueryNameFor(pinned, a).StartsWith(musician, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        BaseItem? tier3Fuzzy = FuzzyMatch(musician, fullPrefixCandidates, user, pinned);
                        tierSw.Stop();
                        tierReached = 3;
                        logger.LogInformation(
                            "ArtistSearch: tier=3 duration={TierMs}ms matched={Matched} method=InMemoryPrefixFull query='{Query}'",
                            tierSw.ElapsedMilliseconds, tier3Fuzzy != null, musician);
                        if (tier3Fuzzy != null)
                        {
                            artists = new List<BaseItem> { tier3Fuzzy };
                        }
                    }

                    // Tier 1.5 (JF-437): word-coverage tier, shared entry point (runs after
                    // tiers 2-3, before tier 4 (see TryWordCoverageTier for the placement)
                    // rationale). A single result flows through the caller's downstream
                    // judgment (JF-377 downgrade, JF-420 gate) unchanged; ties disambiguate.
                    if (artists.Count == 0 && TryWordCoverageTier(musician, allArtists, locale, logger, out var wordCoverageMatches, pinned))
                    {
                        artists = wordCoverageMatches;
                        tierReached = 4; // tier 1.5 preempted tier 4 (the tier_reached summary log is coarse)
                    }

                    // Tier 4: fuzzy match against ALL artists
                    if (artists.Count == 0)
                    {
                        tierSw.Restart();
                        // JF-417 review correction: do NOT exclude the deferred candidate from
                        // tier-4. The exclusion (original JF-417 approach) fixed "P!nk floyd" ->
                        // Pink Floyd but broke the common "nirvana unplugged" shape (Nirvana was
                        // excluded, "Nirvana Tribute Band" won). The containment exemption still
                        // carries the deferred candidate at ContainmentScore at tier-4; the
                        // P!nk-floyd case is handled by the ALBUM path (PlayAlbumIntent +
                        // catalog-backed AlbumName entity resolution), not the artist path.
                        BaseItem? tier4Fuzzy = FuzzyMatch(musician, allArtists, user, pinned);
                        tierSw.Stop();
                        tierReached = 4;
                        logger.LogInformation(
                            "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=InMemoryFuzzyAll query='{Query}' deferred-excluded={Deferred}",
                            tierSw.ElapsedMilliseconds, tier4Fuzzy != null, musician, deferredTier2Match?.Name);
                        if (tier4Fuzzy != null)
                        {
                            artists = new List<BaseItem> { tier4Fuzzy };
                        }
                    }

                    // JF-417: if tier-2 produced a deferred partial match and tiers 3-4 found
                    // nothing better (the deferred candidate was the ONLY plausible match),
                    // accept the deferred match as the final result. If tier-4 DID produce a
                    // different result (e.g. "Pink Floyd" for query "P!nk floyd"), the tier-4
                    // result wins by not reaching this branch.
                    if (artists.Count == 0 && deferredTier2Match != null)
                    {
                        artists = new List<BaseItem> { deferredTier2Match };
                        logger.LogInformation(
                            "ArtistSearch: falling back to deferred tier-2 match '{Candidate}' for query '{Query}' (tiers 3-4 found nothing better, JF-417)",
                            deferredTier2Match.Name, musician);
                    }
                }
            }
        }
        else
        {
            // Database fallback. Scope assignment; the items-by-name bypass fires
            // automatically inside ApplyLibraryFilter for the MusicArtist kind (the
            // full rationale, folderless artists vs the TopParentIds filter, lives
            // in LibraryFilter.ApplyItemsByNameBypass). JF-457: every tier below
            // post-filters its results through FilterByAlbumScopeAsync, bounding the
            // bypass's wrong-library NAME leak before any name leaves this chain.
            //
            // Tier 1 runs through the ONE ASR variant loop (SearchService's
            // flag-parameterized core, extracted JF-658): the original SearchTerm
            // query first, then the compound-word variants when the caller enables
            // the fix (Thorough only; Fast keeps the single plain query).
            artists = await SearchService.SearchFirstNonEmptyAsync(
                musician,
                searchTerm => dbQuery(BuildArtistQuery(topParentIds, q => q.SearchTerm = searchTerm), cancellationToken),
                asrCompoundWordFixEnabled && mode != SearchResponseMode.Fast).ConfigureAwait(false);

            // JF-381 gate on the raw database results in Thorough mode: SearchTerm
            // matching can surface coincidental substrings, and unlike the
            // in-memory path there is no later phonetic tier over the full index
            // to correct them. The Fast-mode DB exception (CLAUDE.md): Fast has NO
            // recovery tier here (the in-memory Fast path falls through to
            // fuzzy-all, this one does not), so gating would turn direct long-name
            // hits ("florence" -> "Florence + The Machine") into not-founds during
            // the cold-index window; the trade-off is that a cold-start Fast DB
            // search can auto-play a coincidental containment, as it did before
            // the sweep (code-review 2026-08-29).
            if (mode != SearchResponseMode.Fast)
            {
                artists = artists
                    .Where(a => PassesContainmentBand(a.Name, musician))
                    .ToList();
            }

            // JF-457: the tier-1 LIST flows to the caller (not-found messages,
            // disambiguation prompts), so every name must be album-scope verified;
            // an emptied tier naturally continues the chain to tier 2.
            artists = await FilterByAlbumScopeAsync(artists, topParentIds, dbQuery, logger, cancellationToken).ConfigureAwait(false);

            tierSw.Stop();
            tierReached = 1;
            if (mode == SearchResponseMode.Fast)
            {
                logger.LogInformation(
                    "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=SearchTerm query='{Query}' mode=Fast",
                    tierSw.ElapsedMilliseconds, artists.Count, musician);
            }
            else
            {
                logger.LogInformation(
                    "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=SearchTerm query='{Query}'",
                    tierSw.ElapsedMilliseconds, artists.Count, musician);
            }

            // Fast mode DB has no fallback tiers (single query, done); Thorough
            // runs tiers 2-4, either in parallel (the caller's cold-window policy)
            // or sequentially.
            if (artists.Count == 0 && mode != SearchResponseMode.Fast)
            {
                // Tier 2: prefix first word
                string firstWord = FirstWordOf(musician);
                if (parallelDbTiers)
                {
                    // Tiers 2-4 as Task.WhenAll with the 2 > 3 > 4 priority pick,
                    // the inline chain's structure kept verbatim (the JF-315 6b
                    // plan): the tier tasks' retry backoffs overlap, while the
                    // synchronous GetItemList queries themselves share the request
                    // thread. Each tier keeps its own winner-level JF-457 album
                    // scope verification inside PrefixSearchAsync/ContainsSearchAsync.
                    var tier2 = PrefixSearchAsync(firstWord, musician, user, topParentIds, dbQuery, logger, cancellationToken);
                    var tier3 = !string.Equals(firstWord, musician, StringComparison.Ordinal)
                        ? PrefixSearchAsync(musician, musician, user, topParentIds, dbQuery, logger, cancellationToken)
                        : Task.FromResult<IReadOnlyList<BaseItem>>(Array.Empty<BaseItem>());
                    var tier4 = ContainsSearchAsync(musician, user, topParentIds, dbQuery, logger, cancellationToken);

                    IReadOnlyList<BaseItem>[] parallelResults = await Task.WhenAll(tier2, tier3, tier4).ConfigureAwait(false);

                    // Preserve priority: tier 2 > tier 3 > tier 4
                    artists = parallelResults.FirstOrDefault(r => r.Count > 0) ?? (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
                    tierReached = parallelResults[0].Count > 0 ? 2 : parallelResults[1].Count > 0 ? 3 : 4;

                    logger.LogInformation(
                        "ArtistSearch: tiers=2-4 (parallel) matched={Matched} tierHit={Tier} method=ParallelFallback query='{Query}'",
                        artists.Count > 0, tierReached, musician);
                }
                else
                {
                    tierSw.Restart();
                    artists = await PrefixSearchAsync(firstWord, musician, user, topParentIds, dbQuery, logger, cancellationToken).ConfigureAwait(false);
                    tierSw.Stop();
                    tierReached = 2;
                    logger.LogInformation(
                        "ArtistSearch: tier=2 duration={TierMs}ms results={Count} method=PrefixFirstWord query='{Query}' prefix='{Prefix}'",
                        tierSw.ElapsedMilliseconds, artists.Count, musician, firstWord);

                    // Tier 3: prefix full query
                    if (artists.Count == 0 && !string.Equals(firstWord, musician, StringComparison.Ordinal))
                    {
                        tierSw.Restart();
                        artists = await PrefixSearchAsync(musician, musician, user, topParentIds, dbQuery, logger, cancellationToken).ConfigureAwait(false);
                        tierSw.Stop();
                        tierReached = 3;
                        logger.LogInformation(
                            "ArtistSearch: tier=3 duration={TierMs}ms results={Count} method=PrefixFullQuery query='{Query}'",
                            tierSw.ElapsedMilliseconds, artists.Count, musician);
                    }

                    // Tier 4: contains search
                    if (artists.Count == 0)
                    {
                        tierSw.Restart();
                        artists = await ContainsSearchAsync(musician, user, topParentIds, dbQuery, logger, cancellationToken).ConfigureAwait(false);
                        tierSw.Stop();
                        tierReached = 4;
                        logger.LogInformation(
                            "ArtistSearch: tier=4 duration={TierMs}ms results={Count} method=Contains query='{Query}'",
                            tierSw.ElapsedMilliseconds, artists.Count, musician);
                    }
                }
            }
        }

        totalSw.Stop();
        logger.LogInformation(
            "ArtistSearch: total duration={TotalMs}ms tier_reached={Tier} results={Count} query='{Query}' source={Source} mode={Mode}",
            totalSw.ElapsedMilliseconds, tierReached, artists.Count, musician, searchSource, mode);

        return artists;
    }

    /// <summary>
    /// The shared MusicArtist tier query preamble (scope + kind + DtoOptions) with
    /// the tier's shape applied by <paramref name="configure"/> (SearchTerm for
    /// tier 1, NameStartsWith for tiers 2-3, NameContains for tier 4). The JF-658
    /// home of the query-construction idiom the deleted handler-local
    /// TrySearchFallbackAsync carried.
    /// </summary>
    private static InternalItemsQuery BuildArtistQuery(Guid[]? topParentIds, Action<InternalItemsQuery> configure)
    {
        var query = new InternalItemsQuery()
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            DtoOptions = new DtoOptions(true)
        };
        LibraryFilter.ApplyLibraryFilter(query, topParentIds);
        configure(query);
        return query;
    }

    /// <summary>The query's first word (the tier-2 prefix), or the whole query
    /// when it is single-word.</summary>
    private static string FirstWordOf(string query)
        => query.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? query;

    private static async Task<IReadOnlyList<BaseItem>> PrefixSearchAsync(
        string prefix, string musician, Entities.User? user, Guid[]? topParentIds,
        Func<InternalItemsQuery, CancellationToken, Task<IReadOnlyList<BaseItem>>> dbQuery,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var query = BuildArtistQuery(topParentIds, q => q.NameStartsWith = prefix);

        // Null-tolerant like the tier-1 ASR loop: a dbQuery channel may surface a
        // null result set (production GetItemList does not, the JF-658 fold made
        // the handler's former inline null guard live here instead).
        IReadOnlyList<BaseItem> results = await dbQuery(query, cancellationToken).ConfigureAwait(false) ?? Array.Empty<BaseItem>();
        BaseItem? fuzzy = FuzzyMatch(musician, results, user, null);
        return await KeepIfAlbumScopeAsync(fuzzy, topParentIds, dbQuery, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<BaseItem>> ContainsSearchAsync(
        string searchTerm, Entities.User? user, Guid[]? topParentIds,
        Func<InternalItemsQuery, CancellationToken, Task<IReadOnlyList<BaseItem>>> dbQuery,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var query = BuildArtistQuery(topParentIds, q => q.NameContains = searchTerm);

        IReadOnlyList<BaseItem> results = await dbQuery(query, cancellationToken).ConfigureAwait(false) ?? Array.Empty<BaseItem>();

        // JF-381 gate before fuzzy: NameContains is an explicit substring match, so the
        // candidate set itself can be purely coincidental ("cup" -> "Porcupine Tree")
        // and the fuzzy step would happily confirm it at ContainmentScore.
        BaseItem? fuzzy = FuzzyMatch(searchTerm, results.Where(a => PassesContainmentBand(a.Name, searchTerm)).ToList(), user, null);
        return await KeepIfAlbumScopeAsync(fuzzy, topParentIds, dbQuery, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// JF-457 winner-level album-scope verification shared by the prefix/contains DB
    /// tiers: the fuzzy WINNER is verified (one bounded query), not the candidate set,
    /// because prefix-shaped candidate sets are unbounded ("The" matches most of the
    /// catalog) and per-candidate checks would blow the Alexa budget in the very
    /// cold-window this branch exists for. When the winner fails the check the tier
    /// returns empty, so the chain's next tier (or the caller's parallel tiers) runs.
    /// Trade-off: a second-best in-scope candidate behind an out-of-scope winner is
    /// not recovered by the same tier; the warm index re-serves the full scope.
    /// </summary>
    internal static async Task<IReadOnlyList<BaseItem>> KeepIfAlbumScopeAsync(
        BaseItem? fuzzy, Guid[]? topParentIds,
        Func<InternalItemsQuery, CancellationToken, Task<IReadOnlyList<BaseItem>>> dbQuery,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (fuzzy == null)
        {
            return Array.Empty<BaseItem>();
        }

        // The filter already returns exactly [fuzzy] when kept and [] when dropped.
        return await FilterByAlbumScopeAsync(
            new[] { fuzzy }, topParentIds, dbQuery, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detects a coincidental substring-containment match: the candidate name is shorter
    /// than the query, sits inside the query as a substring, yet its words cover fewer than
    /// half of the query's CONTENT words (locale stop words excluded). This is the false-positive
    /// shape from JF-377 (e.g. query "zzzqqq nonexistent artist" vs artist "artist"): the
    /// containment shortcut in FuzzyMatcher.PartialRatio scores it 90, but only one of three
    /// content words belongs to the candidate, so it is unrelated noise rather than an intended
    /// match. Also true for the JF-408 embedded shape: every occurrence of the candidate
    /// embedded inside another word (artist "artist" in "xyznonexistentartist123"; album "O"
    /// via the word-initial 'o' of "of" in "dark side of the moon", JF-478), which the
    /// coverage rule cannot see in single-content-word queries and cannot see at all when
    /// the candidate itself tokenizes to nothing (a stop-word-only name like "O" under
    /// it-IT, where "o" is the conjunction). Returns false for every legitimate shape:
    /// candidate at least as long as the query (ASR truncation), candidate not a substring of the
    /// query (genuine fuzzy-distance match), the candidate's words covering at least half the
    /// query's content words, or a containment that occurs as a whole word somewhere or a
    /// plausible affix form (whole-word or affixed forms like "outkasts" -> "outkast").
    /// <para>
    /// This NARROWS the JF-342 invariant in FuzzyMatcher.ApplyLengthPenalty (which exempts ALL
    /// contained candidates from the length penalty as "a real near-exact match"): the low-coverage
    /// subcase detected here is the exception where that assumption fails. The exemption in
    /// FuzzyManager itself is intentionally left intact (broad blast radius); this predicate is the
    /// caller-side refinement applied at the artist tier-4 single-match decision point.
    /// </para>
    /// </summary>
    /// <param name="locale">Locale used to strip carrier/grammar stop words before computing
    /// coverage. This is essential: the <c>musician</c> slot carries raw spoken text (CLAUDE.md
    /// gotcha), so carrier phrases like it-IT "suona la musica di {artist}" bleed into the query.
    /// Without stop-word stripping a real single-word artist ("Bush") inside that carrier would
    /// cover only 1 of 5 raw words and be wrongly rejected. <see cref="KeywordMatcher.Tokenize"/>
    /// handles en/it/de/fr/es/pt; unknown locales (ja/ar/hi) get no stripping (documented-weak
    /// fallback, JF-337 AC #4).</param>
    internal static bool IsCoincidentalContainmentMatch(string query, string candidateName, string? locale = null)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidateName))
        {
            return false;
        }

        string q = query.Trim();
        string c = candidateName.Trim();

        // Only the short-candidate-inside-long-query shape is suspect. A candidate at least
        // as long as the query is the intended ASR-truncation / full-name case.
        if (c.Length >= q.Length)
        {
            return false;
        }

        // Must actually be a containment match (the 90-score shortcut path). If the candidate
        // is not a substring, the match came from Levenshtein distance and is genuine.
        if (q.IndexOf(c, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        // JF-408 residual (found via the simulator on the deployed build): a containment whose
        // every occurrence is EMBEDDED in another word (strictly interior, or a word-edge
        // fragment that is not a plausible affix form) is riding inside another word
        // ("artist" inside "xyznonexistentartist123" auto-played a garbage-metadata artist;
        // "O" via the 'o' of "of"/"moon" in "dark side of the moon" auto-played Damien
        // Rice's album, JF-478). The coverage rule below cannot see this in single-token
        // queries (fewer than 2 content words short-circuits to "not coincidental") and
        // cannot see it at all for stop-word-only candidate names, so the embedded shape is
        // detected here. Whole-word and plausible-affix shapes ("outkasts" -> "outkast",
        // plural or affixed real names) are not embedded and fall through to the coverage
        // rule so legit affixed matches keep auto-playing.
        if (HasOnlyCoincidentalOccurrences(q, c))
        {
            return true;
        }

        // Coverage = fraction of the query's CONTENT words (stop words excluded) that appear
        // among the candidate's content words. KeywordMatcher.Tokenize strips locale carrier/
        // grammar words (so it-IT "suona la musica di bush" -> [bush], not [suona, la, musica,
        // di, bush]) and splits on non-alphanumerics (trailing punctuation does not break the
        // match). Without stop-word stripping a real artist inside a carrier phrase is wrongly
        // rejected (code-review JF-377 regression).
        string loc = locale ?? string.Empty;
        var queryTokens = KeywordMatcher.Tokenize(q, loc);
        if (queryTokens.Length < 2)
        {
            // Fewer than 2 content words can't be coincidental (no unrelated content to hide in).
            return false;
        }

        var candidateTokens = new HashSet<string>(
            KeywordMatcher.Tokenize(c, loc),
            StringComparer.OrdinalIgnoreCase);
        if (candidateTokens.Count == 0)
        {
            // Candidate has no content tokens (e.g. a stop-word-only name). Coverage is
            // undefined; do not reject.
            return false;
        }

        int covered = queryTokens.Count(t => candidateTokens.Contains(t));

        // Reject only when the candidate covers a minority (strictly under half) of the query's
        // content words. At-or-above half is a plausible multi-word near-match and is kept.
        return covered * 2 < queryTokens.Length;
    }

    /// <summary>
    /// Whether the candidate name occurs in the query only EMBEDDED inside other words:
    /// never as a whole word, and never as a plausible affix form of a longer word
    /// (plural/inflection, where the containing word extends the candidate by at most
    /// <see cref="AffixOverflowTolerance"/> characters and the candidate itself is at
    /// least <see cref="MinAffixCandidateLength"/> characters). This is the coincidental
    /// containment shape: JF-408 (album "O" via the strictly-interior 'o' in
    /// "walls for cup"; artist "artist" inside "xyznonexistentartist123") and JF-478
    /// (album "O" via the word-INITIAL 'o' of "of" inside "dark side of the moon",
    /// where the occurrences are boundary-touching fragments yet the user never spoke
    /// the name as a word). Whole-word occurrences ("u2" in "un disco di u2") and
    /// affixed occurrences ("outkasts" -> "outkast") are legit and return false. Used
    /// by auto-play decision points that have no full word-coverage predicate (the
    /// PlayAlbum fuzzy fallback and the song-to-album cascade); the artist path uses
    /// the richer <see cref="IsCoincidentalContainmentMatch"/>.
    /// </summary>
    /// <param name="query">The raw query string.</param>
    /// <param name="candidateName">The matched candidate's name.</param>
    /// <returns>True when the candidate is contained and every occurrence is embedded in another word.</returns>
    internal static bool IsEmbeddedContainment(string query, string candidateName)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidateName))
        {
            return false;
        }

        return HasOnlyCoincidentalOccurrences(query.Trim(), candidateName.Trim());
    }

    /// <summary>
    /// How many characters a containing word may extend beyond a boundary-touching
    /// occurrence before it stops being a plausible affix form (plural "s"/"es",
    /// possessive, vowel inflection) and becomes an unrelated word the candidate is a
    /// fragment of. JF-478.
    /// </summary>
    internal const int AffixOverflowTolerance = 2;

    /// <summary>
    /// Minimum candidate length for an affix form to be plausible: 1-2 character names
    /// have no inflection class, so any boundary-touching occurrence of them inside a
    /// LONGER word is a fragment ("O" at the start of "of"), never a plural. JF-478.
    /// </summary>
    internal const int MinAffixCandidateLength = 3;

    /// <summary>
    /// Whether every occurrence of <paramref name="needle"/> in <paramref name="haystack"/>
    /// is embedded in another word: not a whole word, and not a plausible affix form.
    /// Strictly interior occurrences (word characters on both sides) are always
    /// embedded; an occurrence at a word edge is embedded unless the containing word
    /// extends the candidate by at most <see cref="AffixOverflowTolerance"/> characters
    /// with a candidate at least <see cref="MinAffixCandidateLength"/> long (the
    /// "outkasts" -> "outkast" plural class). See <see cref="IsCoincidentalContainmentMatch"/>.
    /// </summary>
    /// <param name="haystack">The query string.</param>
    /// <param name="needle">The candidate name.</param>
    /// <returns>True when all occurrences are embedded; false when there is none or any is a whole word or plausible affix.</returns>
    private static bool HasOnlyCoincidentalOccurrences(string haystack, string needle)
    {
        int index = 0;
        bool any = false;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            any = true;

            if (IsRealWordOccurrence(haystack, needle, index))
            {
                return false;
            }

            index++;
        }

        return any;
    }

    /// <summary>
    /// Whether the occurrence of <paramref name="needle"/> at <paramref name="index"/>
    /// reads as the user having SPOKEN the name: a whole-word occurrence (non-word
    /// characters or the string boundaries on both sides), or a plausible affix form
    /// of a single word (the word extends the candidate by a short tail, e.g. the
    /// plural "outkasts" for "outkast"). Everything else, including word-initial and
    /// word-final fragments of longer words ("O" at the start of "of", at the end of
    /// "glow"), is embedded in another word and carries no user intent. JF-478.
    /// </summary>
    /// <param name="haystack">The query string.</param>
    /// <param name="needle">The candidate name.</param>
    /// <param name="index">The start of this occurrence of the needle.</param>
    /// <returns>True when this occurrence is a whole word or a plausible affix form.</returns>
    private static bool IsRealWordOccurrence(string haystack, string needle, int index)
    {
        bool leftIsWordChar = index > 0 && char.IsLetterOrDigit(haystack[index - 1]);
        int end = index + needle.Length;
        bool rightIsWordChar = end < haystack.Length && char.IsLetterOrDigit(haystack[end]);

        if (!leftIsWordChar && !rightIsWordChar)
        {
            return true;
        }

        if (leftIsWordChar && rightIsWordChar)
        {
            // Strictly interior: always embedded, never a spoken form.
            return false;
        }

        // Edge occurrence (prefix or suffix of a longer word): legit only as a
        // plausible affix form, which needs a candidate long enough to dominate the
        // word (1-2 char names have no inflection class).
        if (needle.Length < MinAffixCandidateLength)
        {
            return false;
        }

        int wordStart = index;
        while (wordStart > 0 && char.IsLetterOrDigit(haystack[wordStart - 1]))
        {
            wordStart--;
        }

        int wordEnd = end;
        while (wordEnd < haystack.Length && char.IsLetterOrDigit(haystack[wordEnd]))
        {
            wordEnd++;
        }

        return (wordEnd - wordStart) - needle.Length <= AffixOverflowTolerance;
    }

    private static BaseItem? FuzzyMatch(string query, IReadOnlyList<BaseItem> candidates, Entities.User? user,
        IArtistIndex? artistIndex)
    {
        int threshold = FuzzyMatcher.GetDefaultThreshold(user);

        // Use phonetic-enhanced matching when artist index (with pre-computed codes) is available.
        // JF-755: both overloads score through QueryNameFor when the index is present, so a
        // kana-named artist competes by its romaji key against the always-romanized query
        // (exact 100 for the romanized canonical, clear of the 91-floor Latin tie).
        if (artistIndex?.IsReady == true)
        {
            return FuzzyMatcher.FindBestMatch(
                query,
                candidates,
                a => QueryNameFor(artistIndex, a),
                a => a.Id,
                id =>
                {
                    if (artistIndex.TryGetPhoneticCode(id, out var codes))
                    {
                        return codes;
                    }

                    return null;
                },
                threshold);
        }

        return FuzzyMatcher.FindBestMatch(query, candidates, a => a.Name, threshold);
    }
}

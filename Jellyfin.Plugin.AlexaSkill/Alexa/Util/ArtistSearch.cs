using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Shared artist search with in-memory index preference and database fallback.
/// Uses the same 4-tier strategy as PlayArtistSongsIntentHandler:
///   1. Name contains (in-memory) / SearchTerm (database)
///   2. Prefix first word + fuzzy
///   3. Prefix full query + fuzzy
///   4. Fuzzy match against all artists
/// </summary>
internal static class ArtistSearch
{
    /// <summary>
    /// JF-381 tier-1 containment gate: a candidate name longer than the query by more
    /// than this many characters is a coincidental substring ("cup" in "Porcupine Tree"),
    /// not an intended match; the fuzzy/phonetic tiers handle the accent drift instead.
    /// Shared with the inline PlayArtistSongs search so the two paths cannot drift.
    /// </summary>
    internal const int Tier1ContainmentLengthBand = 10;

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
    /// JF-419.2 warming gate, layer 2 of 2. Layer 1 is the per-handler entry call
    /// (see e.g. PlaySongIntentHandler): handlers that run cold NON-artist queries
    /// first (title/album/keyword/mood searches on the same cold database) refuse at
    /// entry, before their "searching" announcement. This choke point covers every
    /// ArtistSearch caller including the ones with no entry gate (BaseHandler
    /// fallbacks, future handlers). While the in-memory index is present but still
    /// loading, a search would fall through to the cold database path that can exceed
    /// Alexa's ~8-second window (live incident 2026-08-31 07:59); throwing lets the
    /// request pipeline answer with the SkillWarmingUp Tell for every intent at once.
    /// Enrichment-only callers may catch it and degrade (see MediaInfo).
    /// </summary>
    internal static void EnsureIndexReady(IArtistIndex? artistIndex)
    {
        if (artistIndex != null && !artistIndex.IsReady)
        {
            throw new SkillWarmingUpException();
        }
    }

    public static async Task<IReadOnlyList<BaseItem>> SearchAsync(
        string musician,
        Entities.User? user,
        ILibraryManager libraryManager,
        IArtistIndex? artistIndex,
        ILogger logger,
        Func<InternalItemsQuery, CancellationToken, Task<IReadOnlyList<BaseItem>>> dbQuery,
        CancellationToken cancellationToken)
    {
        EnsureIndexReady(artistIndex);

        var totalSw = Stopwatch.StartNew();
        var tierSw = Stopwatch.StartNew();
        int tierReached = 0;
        string searchSource = "Database";

        IReadOnlyList<BaseItem> artists;

        if (artistIndex?.IsReady == true)
        {
            searchSource = "InMemory";
            var allowedLibraryIds = LibraryFilter.GetAllowedLibraryIds(user);
            Guid[]? topParentIds = allowedLibraryIds != null
                ? LibraryFilter.ResolveTopParentIds(allowedLibraryIds, libraryManager)
                : null;
            var allArtists = artistIndex.GetArtists(topParentIds);

            // Tier 1: name contains query, with the JF-381 coincidental-containment gate.
            // Without the gate a short query inside a long name wins tier 1 and stops the
            // chain (live 2026-08-28 21:17: ASR "cup" for "Koop" returned "Porcupine Tree"
            // here and the album path played the wrong artist, while the inline
            // PlayArtistSongs search, which HAS the gate, correctly fell through to the
            // phonetic tier). Gating lets tiers 2-4 resolve the accent drift instead.
            tierSw.Restart();
            artists = allArtists
                .Where(a => a.Name.Contains(musician, StringComparison.OrdinalIgnoreCase)
                    && PassesContainmentBand(a.Name, musician))
                .ToList();
            tierSw.Stop();
            tierReached = 1;
            logger.LogInformation(
                "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=InMemoryContains query='{Query}'",
                tierSw.ElapsedMilliseconds, artists.Count, musician);

            // Tier 2: prefix first word + fuzzy
            string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;
            BaseItem? deferredTier2Match = null;
            if (artists.Count == 0)
            {
                tierSw.Restart();
                var prefixCandidates = allArtists
                    .Where(a => a.Name.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                BaseItem? fuzzy = FuzzyMatch(musician, prefixCandidates, user, artistIndex);
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
            }

            // Tier 3: prefix full query + fuzzy
            if (artists.Count == 0 && !string.Equals(firstWord, musician, StringComparison.Ordinal))
            {
                tierSw.Restart();
                var prefixCandidates = allArtists
                    .Where(a => a.Name.StartsWith(musician, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                BaseItem? fuzzy = FuzzyMatch(musician, prefixCandidates, user, artistIndex);
                tierSw.Stop();
                tierReached = 3;
                logger.LogInformation(
                    "ArtistSearch: tier=3 duration={TierMs}ms matched={Matched} method=InMemoryPrefixFull query='{Query}'",
                    tierSw.ElapsedMilliseconds, fuzzy != null, musician);
                if (fuzzy != null)
                {
                    artists = new List<BaseItem> { fuzzy };
                }
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
                BaseItem? fuzzy = FuzzyMatch(musician, allArtists, user, artistIndex);
                tierSw.Stop();
                tierReached = 4;
                logger.LogInformation(
                    "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=InMemoryFuzzyAll query='{Query}' deferred-excluded={Deferred}",
                    tierSw.ElapsedMilliseconds, fuzzy != null, musician, deferredTier2Match?.Name);
                if (fuzzy != null)
                {
                    artists = new List<BaseItem> { fuzzy };
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
        else
        {
            // Database fallback
            var query = new InternalItemsQuery()
            {
                Recursive = true,
                SearchTerm = musician,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                DtoOptions = new DtoOptions(true)
            };
            LibraryFilter.ApplyLibraryFilter(query, user, libraryManager);

            // JF-381 gate on the raw database results too: SearchTerm matching can surface
            // coincidental substrings, and unlike the in-memory path there is no later
            // phonetic tier over the full index to correct them.
            artists = (await dbQuery(query, cancellationToken).ConfigureAwait(false))
                .Where(a => PassesContainmentBand(a.Name, musician))
                .ToList();

            tierSw.Stop();
            tierReached = 1;
            logger.LogInformation(
                "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=SearchTerm query='{Query}'",
                tierSw.ElapsedMilliseconds, artists.Count, musician);

            // Tier 2: prefix first word
            string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;
            if (artists.Count == 0)
            {
                tierSw.Restart();
                artists = await PrefixSearchAsync(firstWord, musician, user, libraryManager, dbQuery, cancellationToken).ConfigureAwait(false);
                tierSw.Stop();
                tierReached = 2;
                logger.LogInformation(
                    "ArtistSearch: tier=2 duration={TierMs}ms results={Count} method=PrefixFirstWord query='{Query}' prefix='{Prefix}'",
                    tierSw.ElapsedMilliseconds, artists.Count, musician, firstWord);
            }

            // Tier 3: prefix full query
            if (artists.Count == 0 && !string.Equals(firstWord, musician, StringComparison.Ordinal))
            {
                tierSw.Restart();
                artists = await PrefixSearchAsync(musician, musician, user, libraryManager, dbQuery, cancellationToken).ConfigureAwait(false);
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
                artists = await ContainsSearchAsync(musician, user, libraryManager, dbQuery, cancellationToken).ConfigureAwait(false);
                tierSw.Stop();
                tierReached = 4;
                logger.LogInformation(
                    "ArtistSearch: tier=4 duration={TierMs}ms results={Count} method=Contains query='{Query}'",
                    tierSw.ElapsedMilliseconds, artists.Count, musician);
            }
        }

        totalSw.Stop();
        logger.LogInformation(
            "ArtistSearch: total duration={TotalMs}ms tier_reached={Tier} results={Count} query='{Query}' source={Source}",
            totalSw.ElapsedMilliseconds, tierReached, artists.Count, musician, searchSource);

        return artists;
    }

    private static async Task<IReadOnlyList<BaseItem>> PrefixSearchAsync(
        string prefix, string musician, Entities.User? user, ILibraryManager libraryManager,
        Func<InternalItemsQuery, CancellationToken, Task<IReadOnlyList<BaseItem>>> dbQuery,
        CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery()
        {
            Recursive = true,
            NameStartsWith = prefix,
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            DtoOptions = new DtoOptions(true)
        };
        LibraryFilter.ApplyLibraryFilter(query, user, libraryManager);

        IReadOnlyList<BaseItem> results = await dbQuery(query, cancellationToken).ConfigureAwait(false);
        BaseItem? fuzzy = FuzzyMatch(musician, results, user, null);
        return fuzzy != null ? new List<BaseItem> { fuzzy } : Array.Empty<BaseItem>();
    }

    private static async Task<IReadOnlyList<BaseItem>> ContainsSearchAsync(
        string searchTerm, Entities.User? user, ILibraryManager libraryManager,
        Func<InternalItemsQuery, CancellationToken, Task<IReadOnlyList<BaseItem>>> dbQuery,
        CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery()
        {
            Recursive = true,
            NameContains = searchTerm,
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            DtoOptions = new DtoOptions(true)
        };
        LibraryFilter.ApplyLibraryFilter(query, user, libraryManager);

        IReadOnlyList<BaseItem> results = await dbQuery(query, cancellationToken).ConfigureAwait(false);

        // JF-381 gate before fuzzy: NameContains is an explicit substring match, so the
        // candidate set itself can be purely coincidental ("cup" -> "Porcupine Tree")
        // and the fuzzy step would happily confirm it at ContainmentScore.
        BaseItem? fuzzy = FuzzyMatch(searchTerm, results.Where(a => PassesContainmentBand(a.Name, searchTerm)).ToList(), user, null);
        return fuzzy != null ? new List<BaseItem> { fuzzy } : Array.Empty<BaseItem>();
    }

    /// <summary>
    /// Detects a coincidental substring-containment match: the candidate name is shorter
    /// than the query, sits inside the query as a substring, yet its words cover fewer than
    /// half of the query's CONTENT words (locale stop words excluded). This is the false-positive
    /// shape from JF-377 (e.g. query "zzzqqq nonexistent artist" vs artist "artist"): the
    /// containment shortcut in FuzzyMatcher.PartialRatio scores it 90, but only one of three
    /// content words belongs to the candidate, so it is unrelated noise rather than an intended
    /// match. Also true for the JF-408 interior shape: every occurrence of the candidate strictly
    /// inside another word (artist "artist" in "xyznonexistentartist123"), which the coverage
    /// rule cannot see in single-content-word queries. Returns false for every legitimate shape:
    /// candidate at least as long as the query (ASR truncation), candidate not a substring of the
    /// query (genuine fuzzy-distance match), the candidate's words covering at least half the
    /// query's content words, or a containment that touches a token boundary somewhere (whole-word
    /// or affixed forms like "outkasts" -> "outkast").
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
        // every occurrence is strictly INTERIOR (word characters on both sides) is riding
        // inside another word ("artist" inside "xyznonexistentartist123" auto-played a
        // garbage-metadata artist). The coverage rule below cannot see this in single-token
        // queries (fewer than 2 content words short-circuits to "not coincidental"), so the
        // interior shape is detected here. Prefix/suffix shapes ("outkasts" -> "outkast",
        // plural or affixed real names) are NOT interior and fall through to the coverage
        // rule so legit affixed matches keep auto-playing.
        if (HasOnlyInteriorOccurrences(q, c))
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
    /// Whether the candidate name occurs in the query ONLY strictly inside other words
    /// (word characters on both sides of every occurrence). This is the coincidental
    /// containment shape (JF-408): album "O" via the 'o' in "walls for cup", artist
    /// "artist" inside "xyznonexistentartist123". Whole-word and affixed occurrences
    /// ("outkasts" -> "outkast") are boundary-touching and return false. Used by
    /// auto-play decision points that have no full word-coverage predicate (the
    /// PlayAlbum fuzzy fallback); the artist path uses the richer
    /// <see cref="IsCoincidentalContainmentMatch"/>.
    /// </summary>
    /// <param name="query">The raw query string.</param>
    /// <param name="candidateName">The matched candidate's name.</param>
    /// <returns>True when the candidate is contained and every occurrence is interior.</returns>
    internal static bool IsInteriorContainment(string query, string candidateName)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidateName))
        {
            return false;
        }

        return HasOnlyInteriorOccurrences(query.Trim(), candidateName.Trim());
    }

    /// <summary>
    /// Whether every occurrence of <paramref name="needle"/> in <paramref name="haystack"/>
    /// is strictly interior: word characters on BOTH sides (an occurrence touching a token
    /// boundary, including prefix/suffix of a longer word such as the plural form
    /// "outkasts" -> "outkast", disqualifies). See <see cref="IsCoincidentalContainmentMatch"/>.
    /// </summary>
    /// <param name="haystack">The query string.</param>
    /// <param name="needle">The candidate name.</param>
    /// <returns>True when all occurrences are interior; false when there is none or any is boundary-touching.</returns>
    private static bool HasOnlyInteriorOccurrences(string haystack, string needle)
    {
        int index = 0;
        bool any = false;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            any = true;
            bool leftIsWordChar = index > 0 && char.IsLetterOrDigit(haystack[index - 1]);
            int end = index + needle.Length;
            bool rightIsWordChar = end < haystack.Length && char.IsLetterOrDigit(haystack[end]);

            if (!(leftIsWordChar && rightIsWordChar))
            {
                return false;
            }

            index++;
        }

        return any;
    }

    private static BaseItem? FuzzyMatch(string query, IReadOnlyList<BaseItem> candidates, Entities.User? user,
        IArtistIndex? artistIndex)
    {
        int threshold = FuzzyMatcher.GetDefaultThreshold(user);

        // Use phonetic-enhanced matching when artist index (with pre-computed codes) is available
        if (artistIndex?.IsReady == true)
        {
            return FuzzyMatcher.FindBestMatch(
                query,
                candidates,
                a => a.Name,
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

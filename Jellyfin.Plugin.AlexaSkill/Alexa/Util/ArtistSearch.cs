using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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
    public static async Task<IReadOnlyList<BaseItem>> SearchAsync(
        string musician,
        Entities.User? user,
        ILibraryManager libraryManager,
        IArtistIndex? artistIndex,
        ILogger logger,
        Func<InternalItemsQuery, CancellationToken, Task<IReadOnlyList<BaseItem>>> dbQuery,
        CancellationToken cancellationToken)
    {
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

            // Tier 1: name contains query
            tierSw.Restart();
            artists = allArtists
                .Where(a => a.Name.Contains(musician, StringComparison.OrdinalIgnoreCase))
                .ToList();
            tierSw.Stop();
            tierReached = 1;
            logger.LogInformation(
                "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=InMemoryContains query='{Query}'",
                tierSw.ElapsedMilliseconds, artists.Count, musician);

            // Tier 2: prefix first word + fuzzy
            string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;
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
                    artists = new List<BaseItem> { fuzzy };
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
                BaseItem? fuzzy = FuzzyMatch(musician, allArtists, user, artistIndex);
                tierSw.Stop();
                tierReached = 4;
                logger.LogInformation(
                    "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=InMemoryFuzzyAll query='{Query}'",
                    tierSw.ElapsedMilliseconds, fuzzy != null, musician);
                if (fuzzy != null)
                {
                    artists = new List<BaseItem> { fuzzy };
                }
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

            artists = await dbQuery(query, cancellationToken).ConfigureAwait(false);

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
        BaseItem? fuzzy = FuzzyMatch(searchTerm, results, user, null);
        return fuzzy != null ? new List<BaseItem> { fuzzy } : Array.Empty<BaseItem>();
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

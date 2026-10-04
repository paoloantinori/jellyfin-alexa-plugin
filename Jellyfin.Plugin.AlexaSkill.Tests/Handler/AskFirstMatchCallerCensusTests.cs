using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-735: the AskFirstMatch CALLER CENSUS as a TEST instead of prose. The
/// decline verdict (AskFirstMatch keeps truncating its disambiguation state at
/// DisambiguationHelper.FirstMatchStateCap) rests on a premise about the
/// callers: every multi-candidate caller except PlayAlbum's direct-search leg
/// reaches the ask through HandleFuzzyMiss's NotFound outcome (so its list is
/// sub-SuggestionThreshold among SCORED candidates; the narrow unscored
/// length-band corner is documented on FirstMatchStateCap), and nine of the
/// ten multi-candidate callers pre-truncate with their own Take(3). A NEW
/// caller can silently break that premise, and the state-cap pins would then
/// resist the correct fix. This roster converts that drift into a loud
/// failure: the plugin assembly's IL is scanned for call/callvirt sites
/// targeting any AskFirstMatch overload (both overloads' tokens are pooled,
/// so the roster tracks WHICH TYPES call, not which overload each site uses),
/// the scan counts CALL SITES, so two sites inside one method body count as
/// two, attributed to their top-level declaring type, and compared against
/// the expected census below in BOTH directions. When this fails, re-weigh
/// the census on FirstMatchStateCap's doc before adding the new caller here
/// (a caller that is NOT NotFound-gated or does NOT pre-truncate may belong
/// in the JF-743 caller-side family instead). The IL walking lives in the
/// shared <see cref="IlCallScanner"/> (the WarmingGateCoverageTests pattern).
/// </summary>
public class AskFirstMatchCallerCensusTests
{
    /// <summary>
    /// The expected AskFirstMatch call-site census: top-level declaring type
    /// name to call-site count (13 sites, 10 types). The single-candidate
    /// downgrade sites (PlayArtistSongs' JF-377/JF-382 gate, PlayAlbum's
    /// JF-473 parity gate, PlayPodcast's JF-640 cross-type guard) and the
    /// multi-candidate sites are counted together; the per-site roles are
    /// documented on FirstMatchStateCap.
    /// </summary>
    private static readonly Dictionary<string, int> ExpectedCallSites = new()
    {
        [nameof(AlbumPlayService)] = 1,
        [nameof(PlayAlbumIntentHandler)] = 2,
        [nameof(PlayPodcastIntentHandler)] = 2,
        [nameof(PlayArtistSongsIntentHandler)] = 2,
        [nameof(SearchMediaIntentHandler)] = 1,
        [nameof(PlaySongIntentHandler)] = 1,
        [nameof(AddToQueueIntentHandler)] = 1,
        [nameof(PlayVideoIntentHandler)] = 1,
        [nameof(PlayBookIntentHandler)] = 1,
        [nameof(PlayNextIntentHandler)] = 1
    };

    [Fact]
    public void AskFirstMatchCallSites_MatchExpectedCensus()
    {
        Dictionary<string, int> discovered = ScanCallSites();

        var listedButMissing = new SortedSet<string>(
            ExpectedCallSites.Where(kv => !discovered.ContainsKey(kv.Key) || discovered[kv.Key] != kv.Value)
                .Select(kv => discovered.TryGetValue(kv.Key, out int actual) && actual != kv.Value
                    ? $"{kv.Key} (expected {kv.Value}, found {actual})"
                    : kv.Key));
        var foundButNotListed = new SortedSet<string>(
            discovered.Where(kv => !ExpectedCallSites.ContainsKey(kv.Key))
                .Select(kv => $"{kv.Key} ({kv.Value} site(s))"));

        Assert.True(
            listedButMissing.Count == 0 && foundButNotListed.Count == 0,
            "AskFirstMatch caller census drifted from the assembly scan. " +
            "The FirstMatchStateCap truncation verdict rests on this census (see its doc); " +
            "re-weigh there before updating the roster. " +
            (listedButMissing.Count > 0
                ? $"Roster entries that no longer match: [{string.Join(", ", listedButMissing)}]. "
                : string.Empty) +
            (foundButNotListed.Count > 0
                ? $"Callers NOT in the census (must be NotFound-gated or pre-truncating, or filed with JF-743): [{string.Join(", ", foundButNotListed)}]."
                : string.Empty));
    }

    /// <summary>
    /// Every AskFirstMatch call/callvirt site in the plugin assembly, counted
    /// per site (not per calling method: two sites can share one method body,
    /// e.g. PlayPodcast's two legs inside HandleAsync) and attributed to the
    /// top-level declaring type, so calls inside async state machines and
    /// closures land on their owning handler or service.
    /// </summary>
    private static Dictionary<string, int> ScanCallSites()
    {
        Assembly pluginAssembly = typeof(DisambiguationHelper).Assembly;
        var targetTokens = new HashSet<int>(
            IlCallScanner.MethodTokens(typeof(DisambiguationHelper), nameof(DisambiguationHelper.AskFirstMatch)));

        var counts = new Dictionary<string, int>();
        foreach ((Type type, MethodBase method) in IlCallScanner.DeclaredMethods(pluginAssembly))
        {
            int sites = IlCallScanner.CallTokens(method).Count(targetTokens.Contains);
            if (sites == 0)
            {
                continue;
            }

            string owner = IlCallScanner.TopLevelType(type).Name;
            counts.TryGetValue(owner, out int current);
            counts[owner] = current + sites;
        }

        return counts;
    }
}

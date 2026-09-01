using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayArtistSongsIntent requests.
/// </summary>
public class PlayArtistSongsIntentHandler : BaseHandler
{
    /// <summary>
    /// JF-420: minimum fair-score margin for auto-selecting the full-name alternative
    /// over a containment match. Below this margin, the handler disambiguates instead.
    /// Derived from the P!nk floyd case: penalized containment 36 vs genuine 90 = 54
    /// margin (auto-select); a tribute band at ~65 vs penalized 37 = 28 margin (also
    /// auto-select); a genuinely close case would be under 20 (disambiguate).
    /// </summary>
    private const double ContainmentVsFullNameMargin = 20.0;

    /// <summary>
    /// JF-420/JF-420.3: the bar for an alternative to be "genuinely plausible",
    /// checked on the RAW score (worth comparing at all: below this the match just
    /// auto-plays) and on the FAIR score (eligible to WIN the comparison: an
    /// exemption-only partial-word hit cannot clear it). One bar, two checkpoints.
    /// </summary>
    private const int AlternativeFullNameThreshold = 80;

    /// <summary>
    /// Whether every word of <paramref name="shorterName"/> also appears as a word of
    /// <paramref name="longerName"/>.
    /// </summary>
    private static bool IsWordSubset(string shorterName, string longerName)
    {
        var longerWords = new HashSet<string>(
            longerName.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        return shorterName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(longerWords.Contains);
    }

    /// <summary>
    /// JF-420.3: the alternative is just a shorter form of the containment match
    /// ("Miles" vs "Miles Davis" for "miles davis live") and adds nothing the user
    /// could have meant, so the comparison is skipped entirely. Guarded on the
    /// match's words appearing in the query (the gate's own substring shape) so a
    /// SUPERSTRING tier-1 match (a tribute act matching "the beatles" by
    /// name-contains-query) never skips.
    /// </summary>
    private static bool IsRedundantShorterForm(string matchName, string alternativeName, string query)
        => IsWordSubset(matchName, query) && IsWordSubset(alternativeName, matchName);

    /// <summary>
    /// JF-420.3 comparison score: the candidate's score scaled by the length-match
    /// fraction in BOTH directions (min(nameLen/queryLen, queryLen/nameLen)).
    /// Deliberately NOT <see cref="FuzzyMatcher.ApplyFairLengthPenalty"/>: the
    /// matcher's 0.5 floor is a RECALL device (keep half-coverage candidates
    /// reachable in general matching), but in this DECISION it manufactured phantom
    /// margins in both directions (review round 2): a containment-exempt
    /// half-query alternative kept 90 ("Floyd" in "p!nk floyd") while a superstring
    /// tribute also kept 90 against a floor-protected match. Decision fairness
    /// wants the honest length fraction on both sides, symmetrically.
    /// </summary>
    private static double FairComparisonScore(string name, string query, int score)
    {
        double ratio = Math.Min((double)name.Length / query.Length, (double)query.Length / name.Length);
        return score * ratio;
    }

    /// <summary>
    /// JF-420.1: exact-name equality between the query and the
    /// single tier-1 candidate (case-insensitive, end-trimmed; the same
    /// normalization as FuzzyMatcher's exact-match concept). The JF-420 gate exists to
    /// resolve CONTAINMENT matches
    /// (artist name inside a LONGER query); equality is the degenerate case where the
    /// containment exemption inflates both sides to ContainmentScore, the margin is
    /// always 0, and an exact request is demoted to a disambiguation prompt (live:
    /// "Soul Coughing" with "Soul Coughing &amp; Roni Size" in the library). An exact
    /// match is the strongest possible signal: it must auto-play. NOT accent-insensitive
    /// by design: an accented query ("måneskin" vs "Måneskin") is ASR accent drift,
    /// which the phonetic tiers exist to resolve.
    /// </summary>
    private static bool IsExactNameMatch(string query, string name)
        => string.Equals(query.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);

    // The JF-381 tier-1 containment band (maximum extra characters a containment
    // candidate may have beyond the query, so "cup" in "Porcupine Tree" cannot short-
    // circuit the search) lives in Util.ArtistSearch as the single shared definition;
    // do not fork it back here.

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager? _queueManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly ISongNgramIndex? _songNgramIndex;

    /// <summary>
    /// JF-439: minimum KeywordMatcher score for the inverse cross-media song
    /// fallback to auto-play. Live calibration (minix, 12766 songs): the WRONG
    /// half-coverage phonetic hit ('rolling stones' -> 'Like a Rolling Stone')
    /// scores ~34; the RIGHT near-full phonetic match ('screenwriters blues' ->
    /// 'Screenwriter's Blues', apostrophe/plural drift) scores ~72; exact full
    /// coverage scores ~105. The bar at 65 keeps 31 points of rejection margin
    /// over the wrong-substitution class and 7 over the legitimate phonetic class.
    /// The forward mirror BaseHandler.TryEntityFallbackAsync gates its fuzzy
    /// scores at 85 for the same reason: a wrong substitution is worse than a
    /// clean not-found (the scales differ, so the bars are not shared).
    /// </summary>
    private const double CrossMediaSongThreshold = 65.0;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayArtistSongsIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    /// <param name="songNgramIndex">Optional in-memory song index for the JF-439 artist-not-found song fallback.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlayArtistSongsIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        ISongNgramIndex? songNgramIndex = null,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _artistIndex = artistIndex;
        _songNgramIndex = songNgramIndex;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayArtistSongs, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play songs from a specific artist.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        string? musician = intentRequest.Intent.Slots?.TryGetValue("musician", out var musicianSlot) == true ? musicianSlot.Value : null;

        Logger.LogDebug("PlayArtistSongs: entered, locale={Locale}", locale);

        if (string.IsNullOrWhiteSpace(musician))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchArtistName", locale));
        }

        // The inline JF-382 search copy bypasses SearchAsync, so apply the same
        // artist gate here AFTER slot validation (a slot-less utterance still gets
        // DidNotCatch) but BEFORE the "searching" progressive response.
        Util.IndexWarmingGate.EnsureReady(_artistIndex);

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var totalSw = Stopwatch.StartNew();
        var tierSw = Stopwatch.StartNew();
        int tierReached = 0;
        string searchSource = "Database";
        SearchResponseMode mode = GetSearchResponseMode(user);

        // Pre-resolve library filter once for the entire request.
        // Used by both the in-memory and database paths, plus the final artist-songs query.
        Guid[]? allowedLibraryIds = null;
        Guid[]? topParentIds = null;

        IReadOnlyList<BaseItem> artists;
        IReadOnlyList<BaseItem>? jf420ArtistPool = null;

        if (_artistIndex?.IsReady == true)
        {
            // In-memory search: resolve library filter once, search the pre-loaded index
            searchSource = "InMemory";
            allowedLibraryIds = GetAllowedLibraryIds(user);
            topParentIds = allowedLibraryIds != null
                ? Util.LibraryFilter.ResolveTopParentIds(allowedLibraryIds, _libraryManager, Logger)
                : null;
            var allArtists = _artistIndex.GetArtists(topParentIds);
            // JF-420 efficiency: reuse this list in the auto-selection check below
            // instead of calling GetArtists again (re-filters + re-allocates for
            // multi-library users).
            jf420ArtistPool = allArtists;

            // Tier 1: name contains query (in-memory equivalent of SearchTerm).
            // Gate: skip containment matches where the query is much shorter than the
            // candidate name, since a short query inside a long name is a coincidental
            // substring (e.g. "cup" in "Porcupine Tree"), not an intended match. This lets
            // the fuzzy/phonetic tiers handle accent drift instead. JF-381.
            tierSw.Restart();
            artists = allArtists
                .Where(a => a.Name.Contains(musician, StringComparison.OrdinalIgnoreCase)
                    && Util.ArtistSearch.PassesContainmentBand(a.Name, musician))
                .ToList();
            tierSw.Stop();
            tierReached = 1;
            Logger.LogInformation(
                "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=InMemoryContains query='{Query}'",
                tierSw.ElapsedMilliseconds, artists.Count, musician);

            if (artists.Count == 0)
            {
                if (mode == SearchResponseMode.Fast)
                {
                    // Fast mode: skip prefix tiers, go straight to fuzzy-all
                    tierSw.Restart();
                    BaseItem? fuzzy = FuzzyMatchPhonetic(musician, allArtists, a => a.Name, a => a.Id, _artistIndex, user);
                    tierSw.Stop();
                    tierReached = 4;
                    Logger.LogInformation(
                        "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=InMemoryFuzzyAll query='{Query}' mode=Fast",
                        tierSw.ElapsedMilliseconds, fuzzy != null, musician);
                    if (fuzzy != null)
                    {
                        artists = new List<BaseItem> { fuzzy };
                    }
                }
                else
                {
                    // Thorough mode: run tiers 2-4 as before (in-memory tiers are sub-ms, no need to parallelize)
                    string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;

                    // Tier 2: prefix first word + fuzzy (catches ASR truncation, e.g. "soul coughin" → "Soul Coughing")
                    tierSw.Restart();
                    var prefixCandidates = allArtists
                        .Where(a => a.Name.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    BaseItem? tier2Match = FuzzyMatchPhonetic(musician, prefixCandidates, a => a.Name, a => a.Id, _artistIndex, user);
                    tierSw.Stop();
                    tierReached = 2;
                    Logger.LogInformation(
                        "ArtistSearch: tier=2 duration={TierMs}ms matched={Matched} method=InMemoryPrefixFirstWord query='{Query}' prefix='{Prefix}'",
                        tierSw.ElapsedMilliseconds, tier2Match != null, musician, firstWord);
                    BaseItem? deferredTier2 = null;
                    if (tier2Match != null)
                    {
                        if (Util.ArtistSearch.IsPartialFirstWordMatch(musician, firstWord, tier2Match.Name))
                        {
                            // JF-417: partial first-word match, defer and let tiers 3-4 run
                            deferredTier2 = tier2Match;
                        }
                        else
                        {
                            artists = new List<BaseItem> { tier2Match };
                        }
                    }

                    // Tier 3: prefix full query + fuzzy (e.g. "Kidz Bop" → "Kidz Bop Kids")
                    if (artists.Count == 0 && !string.Equals(firstWord, musician, StringComparison.Ordinal))
                    {
                        tierSw.Restart();
                        var fullPrefixCandidates = allArtists
                            .Where(a => a.Name.StartsWith(musician, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        BaseItem? tier3Match = FuzzyMatchPhonetic(musician, fullPrefixCandidates, a => a.Name, a => a.Id, _artistIndex, user);
                        tierSw.Stop();
                        tierReached = 3;
                        Logger.LogInformation(
                            "ArtistSearch: tier=3 duration={TierMs}ms matched={Matched} method=InMemoryPrefixFull query='{Query}'",
                            tierSw.ElapsedMilliseconds, tier3Match != null, musician);
                        if (tier3Match != null)
                        {
                            artists = new List<BaseItem> { tier3Match };
                        }
                    }

                // Tier 1.5 (JF-437): word-coverage tier, shared entry point with
                // SearchAsync (placement rationale there). Thorough only: Fast mode
                // keeps its exact pre-tier semantics (speed over recall by design).
                if (mode != SearchResponseMode.Fast && artists.Count == 0
                    && Util.ArtistSearch.TryWordCoverageTier(musician, allArtists, locale, Logger, out var wordCoverageMatches))
                {
                    artists = wordCoverageMatches;
                    tierReached = 4; // tier 1.5 preempted tier 4 (the summary log is coarse)
                }

                    // Tier 4: fuzzy match against ALL artists (catches misspellings)
                    if (artists.Count == 0)
                    {
                        tierSw.Restart();
                        // JF-417 review correction: no exclusion (see ArtistSearch.cs comment)
                        BaseItem? tier4Match = FuzzyMatchPhonetic(musician, allArtists, a => a.Name, a => a.Id, _artistIndex, user);
                        tierSw.Stop();
                        tierReached = 4;
                        Logger.LogInformation(
                            "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=InMemoryFuzzyAll query='{Query}'",
                            tierSw.ElapsedMilliseconds, tier4Match != null, musician);
                        if (tier4Match != null)
                        {
                            artists = new List<BaseItem> { tier4Match };
                        }
                    }

                    // JF-417: fallback to deferred tier-2 if tiers 3-4 found nothing better
                    if (artists.Count == 0 && deferredTier2 != null)
                    {
                        artists = new List<BaseItem> { deferredTier2 };
                    }
                }
            }
        }
        else
        {
            // Fallback: database queries when in-memory index is not yet loaded
            // Resolve library filter once and reuse across all fallback tiers.
            allowedLibraryIds = GetAllowedLibraryIds(user);
            topParentIds = allowedLibraryIds != null
                ? Util.LibraryFilter.ResolveTopParentIds(allowedLibraryIds, _libraryManager, Logger)
                : null;

            if (mode == SearchResponseMode.Fast)
            {
                // Fast mode: single SearchTerm query, no fallback tiers, no ASR variants
                artists = await SearchWithAsrFallbackAsync(musician,
                    searchTerm =>
                    {
                        var q = new InternalItemsQuery()
                        {
                            Recursive = true,
                            SearchTerm = searchTerm,
                            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                            TopParentIds = topParentIds!,
                            DtoOptions = new DtoOptions(true)
                        };
                        return RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", cancellationToken);
                    }, mode).ConfigureAwait(false);

                tierSw.Stop();
                tierReached = 1;
                Logger.LogInformation(
                    "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=SearchTerm query='{Query}' mode=Fast",
                    tierSw.ElapsedMilliseconds, artists.Count, musician);
                // NOTE: no JF-381 containment-band gate here - Fast mode DB has NO
                // recovery tier (the in-memory Fast path falls through to fuzzy-all, this
                // one does not), so gating would turn direct long-name hits ("florence" ->
                // "Florence + The Machine") into not-founds during the cold-index window.
                // Trade-off: a cold-start Fast DB search can auto-play a coincidental
                // containment, as it did before the sweep (code-review 2026-08-29).
            }
            else
            {
                // Thorough mode: 4-tier fallback with ASR variants on tier 1
                artists = await SearchWithAsrFallbackAsync(musician,
                    searchTerm =>
                    {
                        var q = new InternalItemsQuery()
                        {
                            Recursive = true,
                            SearchTerm = searchTerm,
                            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                            TopParentIds = topParentIds!,
                            DtoOptions = new DtoOptions(true)
                        };
                        return RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", cancellationToken);
                    }).ConfigureAwait(false);

                tierSw.Stop();
                tierReached = 1;
                Logger.LogInformation(
                    "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=SearchTerm query='{Query}'",
                    tierSw.ElapsedMilliseconds, artists.Count, musician);
                artists = FilterContainmentBand(artists, musician);

                // Early-exit on tier 1 hit
                if (artists.Count == 0)
                {
                    // Parallelize tiers 2-4: all independent, pick by priority order
                    string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;

                    var tier2 = TryPrefixFallbackAsync(firstWord, musician, topParentIds, user, "GetArtistsFuzzy", cancellationToken);
                    var tier3 = !string.Equals(firstWord, musician, StringComparison.Ordinal)
                        ? TryPrefixFallbackAsync(musician, musician, topParentIds, user, "GetArtistsFullPrefix", cancellationToken)
                        : Task.FromResult<BaseItem?>(null);
                    var tier4 = TryContainsFallbackAsync(musician, musician, topParentIds, user, "GetArtistsContains", cancellationToken);

                    BaseItem?[] parallelResults = await Task.WhenAll(tier2, tier3, tier4).ConfigureAwait(false);

                    // Preserve priority: tier 2 > tier 3 > tier 4
                    BaseItem? match = parallelResults[0] ?? parallelResults[1] ?? parallelResults[2];
                    tierReached = match != null ? (parallelResults[0] != null ? 2 : parallelResults[1] != null ? 3 : 4) : 4;

                    Logger.LogInformation(
                        "ArtistSearch: tiers=2-4 (parallel) matched={Matched} tierHit={Tier} method=ParallelFallback query='{Query}'",
                        match != null, tierReached, musician);
                    if (match != null)
                    {
                        artists = new List<BaseItem> { match };
                    }
                }
            }
        }

        totalSw.Stop();
        Logger.LogInformation(
            "ArtistSearch: total duration={TotalMs}ms tier_reached={Tier} results={Count} query='{Query}' source={Source} mode={Mode}",
            totalSw.ElapsedMilliseconds,
            tierReached,
            artists.Count,
            musician,
            searchSource,
            mode);

        if (artists.Count == 0)
        {
            Logger.LogDebug("PlayArtistSongs: no artist found for query='{Query}'", musician);

            // JF-439 inverse cross-media fallback: the NLU coin-flips musician-shaped
            // song titles ("suona la canzone sugar free jazz" -> musician="sugar free
            // jazz") into this intent when the RepeatSingle collision region resolves
            // to the artist reading. Before giving up, try the song index and serve
            // the song with a FoundSongInstead announcement. Returns null when the
            // fallback does not apply (guard/miss/warming), leaving the clean
            // NotFoundArtist below.
            SkillResponse? songFallback = TrySongFallback(
                musician, user, session, context, locale, cancellationToken);
            if (songFallback != null)
            {
                return songFallback;
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundArtist", locale, musician));
        }

        // JF-377: when a single artist matched with the "coincidental containment" shape (a short
        // name sitting as one content word inside a longer query, detected by
        // ArtistSearch.IsCoincidentalContainmentMatch), do NOT auto-play it silently. The shape is
        // ambiguous: it may be a nonsense query that happened to contain a common-word artist name
        // ("zzzqqq nonexistent artist" -> "artist"), or a real artist hidden in a carrier phrase that
        // bled into the raw slot value ("suona la musica di bush" -> "Bush"). These are
        // string-indistinguishable (research jf377_discriminator_2026-07-26), so instead of silently
        // auto-playing (the bug) or silently rejecting (which regresses the carrier-bleed real-artist
        // case), offer a yes/no prompt. A real artist still plays after the user says "yes"; nonsense
        // resolves to not-found after "no". The check keys on the match SHAPE (not the tier): the
        // predicate returns false for the legitimate shapes that must still auto-play below, among
        // them candidate >= query length (ASR truncation, e.g. "radiohed" -> "Radiohead"), coverage
        // >= half the query content words (a real multi-word near-match), and boundary-touching
        // containments (whole-word or affixed forms like "outkasts" -> "outkast", JF-408).
        if (artists.Count == 1
            && ArtistSearch.IsCoincidentalContainmentMatch(musician, artists[0].Name, locale))
        {
            Logger.LogInformation(
                "PlayArtistSongs: single match='{Match}' for query='{Query}' is coincidental-containment, downgrading to disambiguation (JF-377)",
                artists[0].Name, musician);
            var matches = new List<(Guid Id, string Name, string? ArtUrl)>
            {
                (artists[0].Id, artists[0].Name, GetImageUrl(artists[0].Id.ToString("N"), user))
            };
            return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeArtist, locale, context);
        }

        // JF-420: when the single match is a containment shape (artist name is a substring
        // of a multi-word query, e.g. "P!nk" contained in "P!nk floyd"), check for a
        // DIFFERENT artist that fuzzy-matches the full query above a high threshold.
        // If found, present BOTH as disambiguation candidates directly (NOT via
        // HandleFuzzyMiss, which auto-plays candidates scoring >= ContainmentScore via
        // the containment exemption, which is the exact bug we are fixing). The high
        // threshold (80) ensures "nirvana unplugged" with only "Nirvana Tribute Band"
        // as an alternative (scoring ~65) does NOT trigger: Nirvana auto-plays.
        if (artists.Count == 1
            && musician.Contains(' ')
            && musician.Contains(artists[0].Name, StringComparison.OrdinalIgnoreCase)
            && !IsExactNameMatch(musician, artists[0].Name)
            && _artistIndex?.IsReady == true)
        {
            var searchPool = jf420ArtistPool ?? _artistIndex.GetArtists(topParentIds);
            var alternatives = searchPool.Where(a => !a.Id.Equals(artists[0].Id)).ToList();
            if (alternatives.Count > 0)
            {
                // JF-420.3 (review round 2): score EVERY alternative and rank by FAIR
                // score. FindBestMatchWithScore early-returns on the first candidate
                // reaching ContainmentScore, so a containment-exempt single-word artist
                // earlier in index order ('Floyd' before 'Pink Floyd') masked the true
                // full-name alternative.
                BaseItem? bestAlternative = null;
                double bestAlternativeFair = 0;
                foreach (BaseItem candidate in alternatives)
                {
                    int raw = FuzzyMatcher.Score(musician, candidate.Name);
                    if (raw < AlternativeFullNameThreshold)
                    {
                        continue;
                    }

                    double fair = FairComparisonScore(candidate.Name, musician, raw);
                    if (fair > bestAlternativeFair)
                    {
                        bestAlternativeFair = fair;
                        bestAlternative = candidate;
                    }
                }

                if (bestAlternative != null
                    && !IsRedundantShorterForm(artists[0].Name, bestAlternative.Name, musician))
                {
                    // JF-420/JF-420.3 SYMMETRIC fair comparison (FairComparisonScore:
                    // bidirectional length fraction, no matcher recall floor). The
                    // alternative must ALSO keep a genuinely high fair score: one that
                    // survives only via the containment exemption (a partial-word hit
                    // like "Miles" inside "miles davis live") cannot outrank a better
                    // full match. If the alternative wins by a clear margin, auto-select
                    // it ("P!nk floyd" means Pink Floyd, not P!nk); otherwise offer both.
                    double containmentFair = FairComparisonScore(artists[0].Name, musician, FuzzyMatcher.ContainmentScore);

                    if (bestAlternativeFair >= AlternativeFullNameThreshold && bestAlternativeFair - containmentFair > ContainmentVsFullNameMargin)
                    {
                        Logger.LogInformation(
                            "PlayArtistSongs: containment match '{Containment}' (fair {ContainmentFair:F0}) clearly beaten by full-name alternative '{Alternative}' (fair {AlternativeFair:F0}), auto-selecting (JF-420)",
                            artists[0].Name, containmentFair, bestAlternative.Name, bestAlternativeFair);
                        artists = new List<BaseItem> { bestAlternative };
                    }
                    else
                    {
                        Logger.LogInformation(
                            "PlayArtistSongs: containment match '{Containment}' (fair {ContainmentFair:F0}) vs full-name alternative '{Alternative}' (fair {AlternativeFair:F0}) is ambiguous, disambiguating (JF-420)",
                            artists[0].Name, containmentFair, bestAlternative.Name, bestAlternativeFair);
                        // JF-420.2: Id/Name only, because this branch renders no art (the
                        // only ArtUrl consumer is AskFirstMatch's carousel) and embedding
                        // token-bearing image URLs in session state nothing reads was
                        // pure risk.
                        var matchInfos = new List<DisambiguationHelper.MatchInfo>
                        {
                            new() { Id = artists[0].Id.ToString(), Name = artists[0].Name },
                            new() { Id = bestAlternative.Id.ToString(), Name = bestAlternative.Name }
                        };
                        // Plain name list: the flow is yes/no cycling (yes plays the
                        // first, no advances via DisambiguateNext), so no numbering.
                        // Reprompt is the family's yes/no hint, not the list again.
                        var matchList = string.Join(", ", matchInfos.Select(m => m.Name));
                        string multiPrompt = ResponseStrings.Get("DisambiguateMultipleArtists", locale, matchList);
                        var multiResponse = ResponseBuilder.Ask(multiPrompt, new Reprompt(ResponseStrings.Get("DisambiguateReprompt", locale)));
                        multiResponse.SessionAttributes = new Dictionary<string, object>
                        {
                            [DisambiguationHelper.AttrMatches] = Newtonsoft.Json.JsonConvert.SerializeObject(matchInfos),
                            [DisambiguationHelper.AttrIndex] = 0,
                            [DisambiguationHelper.AttrType] = DisambiguationHelper.MediaTypeArtist
                        };
                        Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.ConversationalFlows.MarkOthersInactive(
                            multiResponse, Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.ConversationalFlows.DisambiguationKeys);
                        return multiResponse;
                    }
                }
            }
        }

        // Disambiguation: in Fast mode, auto-play the best match
        bool fastAutoPlay = mode == SearchResponseMode.Fast && artists.Count > 1;

        if (artists.Count > 1 && !fastAutoPlay)
        {
            Logger.LogDebug("PlayArtistSongs: {Count} artists matched, running disambiguation", artists.Count);
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                musician,
                artists,
                a => a.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeArtist,
                locale,
                best =>
                {
                    artists = new List<BaseItem> { best };
                    return null!;
                },
                user: user);

            if (missOutcome == FuzzyMissOutcome.NotFound)
            {
                Logger.LogDebug("PlayArtistSongs: fuzzy miss outcome=NotFound, asking user to disambiguate");
                var matches = artists.Take(3).Select(a => (a.Id, a.Name, (string?)GetImageUrl(a.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeArtist, locale, context);
            }

            if (missResponse != null)
            {
                Logger.LogDebug("PlayArtistSongs: fuzzy miss outcome={Outcome}, returning response", missOutcome);
                return missResponse;
            }
        }
        else if (fastAutoPlay)
        {
            // Fast mode: pick the best fuzzy match and auto-play
            var best = FuzzyMatchPhonetic(musician, artists, a => a.Name, a => a.Id, _artistIndex, user);
            if (best != null)
            {
                artists = new List<BaseItem> { best };
            }
            else
            {
                artists = new List<BaseItem> { artists[0] };
            }

            Logger.LogDebug("PlayArtistSongs: fast auto-play picked '{Name}'", artists[0].Name);
        }

        string matchedArtistName = artists[0].Name;
        Logger.LogDebug("PlayArtistSongs: matched artist='{ArtistName}' (id={ArtistId})", matchedArtistName, artists[0].Id);

        // Fetch the first page of artist songs for fast time-to-audio.
        // Remaining songs will be fetched on demand by PlaybackNearlyFinished.
        // JF-358: filter via IncludeItemTypes=Audio, NOT MediaTypes=Audio. On Jellyfin 10.11.11,
        // MediaTypes=Audio does not constrain an ArtistIds query (it returns the entire audio
        // library), which makes PopularitySort run over thousands of items and intermittently
        // NRE inside UserDataManager.GetUserData -> RetryAsync burns the 8s Alexa budget.
        var artistSongsQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            OrderBy = PopularitySort,
            DtoOptions = new DtoOptions(true),
            ArtistIds = new[] { artists[0].Id },
            Limit = ProgressiveQueueConstants.GetInitialFetchSize()
        };

        // Reuse pre-resolved library filter. Both paths set topParentIds above.
        if (topParentIds != null)
        {
            artistSongsQuery.TopParentIds = topParentIds;
        }

        // Use GetItemList instead of GetItemsResult. Jellyfin's GetItemsResult evaluates
        // dbQuery.Count() after applying the ArtistIds cross-reference + PopularitySort
        // (which references User data), and EF Core's Count() translation NREs on this
        // combination. GetItemList skips the Count() step entirely.
        IReadOnlyList<BaseItem> artistItems = await RetryAsync(
            () => _libraryManager.GetItemList(artistSongsQuery),
            "GetArtistSongs",
            cancellationToken).ConfigureAwait(false);

        Logger.LogDebug("PlayArtistSongs: Jellyfin returned {SongCount} songs for artist='{ArtistName}'", artistItems.Count, matchedArtistName);

        if (artistItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsForArtist", locale, matchedArtistName));
        }

        // Single-pass sort + resume detection (avoids duplicate GetUserData calls)
        var (artistsItems, startIndex, _) = SortAndFindResumeIndex(
            artistItems, jellyfinUser!, _userDataManager, resumePosition: false);

        if (startIndex > 0)
        {
            Logger.LogInformation(
                "PlayArtistSongs: resuming queue from track {Index} ({Name})",
                startIndex, artistsItems[startIndex].Name);
        }

        if (_config.ShuffleArtistSongs)
        {
            artistsItems = ShuffleCopy(artistsItems);
            startIndex = 0;
            Logger.LogDebug("PlayArtistSongs: shuffled {Count} tracks", artistsItems.Count);
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = startIndex; i < artistsItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = artistsItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = artistsItems[startIndex];

        // Persist queue to device storage for crash recovery
        _queueManager?.SetQueue(
            context.System.Device.DeviceID,
            artistsItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
            0);

        // Store continuation info so PlaybackNearlyFinished can fetch the rest.
        // Without TotalRecordCount, assume more items exist if we filled the page.
        if (artistItems.Count >= ProgressiveQueueConstants.GetInitialFetchSize())
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Artist",
                    ArtistId = artists[0].Id,
                    StartIndex = artistItems.Count,
                    TotalCount = int.MaxValue,
                    UserId = jellyfinUser!.Id,
                    SortOrder = PopularitySort,
                    Shuffle = _config.ShuffleArtistSongs
                });
        }

        string itemId = artistsItems[startIndex].Id.ToString();

        Logger.LogDebug(
            "PlayArtistSongs: returning AudioPlayer, itemId={ItemId}, startIndex={StartIndex}, queueSize={QueueSize}, offset=0",
            itemId, startIndex, queueItems.Count);
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, artistsItems[0], user, context, announceLocale: locale);
    }

    /// <summary>
    /// JF-439 inverse cross-media fallback: no artist matched, so try the song index
    /// with the musician value (the NLU feeds musician-shaped song titles here when
    /// the "Suona la canzone {song}" / "Suona la {musician}" coin flip resolves to
    /// the artist reading). Serves the best song with a FoundSongInstead
    /// announcement; returns null (caller falls through to the clean NotFoundArtist)
    /// when the index is absent/warming (opportunistic enrichment must never worsen
    /// the not-found path), no song passes the keyword-coverage gates, or the best
    /// score sits below <see cref="CrossMediaSongThreshold"/> (review round: a
    /// phonetic half-coverage hit at ~34 must not substitute an unrelated song for
    /// a clean not-found; the forward mirror TryEntityFallbackAsync gates at 85).
    /// No word-count guard BY DESIGN (review round): a spaceless CJK title
    /// tokenizes to one token, so a minimum-token guard would permanently disable
    /// the fallback for ja-JP; the score bar carries the precision burden instead.
    /// </summary>
    private SkillResponse? TrySongFallback(
        string musician,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_songNgramIndex == null)
        {
            return null;
        }

        var keywordTokens = Util.KeywordMatcher.Tokenize(musician, locale);
        if (keywordTokens.Length == 0)
        {
            return null;
        }

        // The index's topParentMap holds parent-chain ROOT ids; GetAllowedLibraryIds
        // returns CONFIGURED collection-folder ids. Resolve through the same walk the
        // artist paths use, or every candidate is filtered out for library-restricted
        // users and the fallback silently no-ops (review round, verified live).
        Guid[]? allowedLibraryIds = GetAllowedLibraryIds(user);
        Guid[]? topParentIds = allowedLibraryIds != null
            ? Util.LibraryFilter.ResolveTopParentIds(allowedLibraryIds, _libraryManager, Logger)
            : null;

        List<(BaseItem Item, double Score)> scored;
        try
        {
            scored = _songNgramIndex.Search(keywordTokens, locale, topParentIds);
            if (scored.Count == 0 && _config.PhoneticSongSearchEnabled)
            {
                scored = _songNgramIndex.SearchPhonetic(keywordTokens, locale, topParentIds);
            }
        }
        catch (SkillWarmingUpException)
        {
            // The warming gate's refusal answers the ORIGINAL artist request; this
            // opportunistic fallback must not convert a not-found into a warming Tell.
            Logger.LogDebug("PlayArtistSongs: song fallback skipped, song index warming");
            return null;
        }

        if (scored.Count == 0 || scored[0].Score < CrossMediaSongThreshold)
        {
            Logger.LogDebug(
                "PlayArtistSongs: song fallback rejected for query='{Query}' (best score {Score:F0} over {Count} candidates, bar={Bar})",
                musician, scored.Count > 0 ? scored[0].Score : 0, scored.Count, CrossMediaSongThreshold);
            return null;
        }

        BaseItem song = scored[0].Item;
        Logger.LogInformation(
            "PlayArtistSongs: song fallback found '{SongName}' itemId={ItemId} (score={Score:F0}) for query='{Query}' (JF-439)",
            song.Name, song.Id, scored[0].Score, musician);

        // One-song queue: same session bookkeeping as the sibling single-song play
        // paths (FindSong/YesIntent), which deliberately skip crash-recovery
        // persistence; the stale-continuation risk of a one-song queue replacing a
        // progressive artist queue is closed by dropping the stored continuation.
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = song.Id } };
        session.FullNowPlayingItem = song;
        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);

        string itemId = song.Id.ToString();
        SkillResponse response = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, song, user, context, announceLocale: locale);
        response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = ResponseStrings.Get("FoundSongInstead", locale, song.Name) };
        return response;
    }

    /// <summary>
    /// Tries a NameStartsWith prefix search followed by fuzzy matching against the results.
    /// Used as a fallback when the primary SearchTerm query returns no artists.
    /// </summary>
    private async Task<BaseItem?> TryPrefixFallbackAsync(
        string prefix, string musician, Guid[]? topParentIds, Entities.User? user,
        string retryLabel, CancellationToken cancellationToken)
    {
        return await TrySearchFallbackAsync(
            q => q.NameStartsWith = prefix, musician, topParentIds, user, retryLabel, applyContainmentBand: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tries a NameContains substring search followed by fuzzy matching against the results.
    /// Catches cases where the query appears anywhere in the artist name (e.g. "Kidz Bop" → "The Kidz Bop Kids").
    /// Since this is a substring-shaped source, results pass through the JF-381 containment
    /// band (a purely coincidental candidate set would be confirmed by the fuzzy step).
    /// </summary>
    private async Task<BaseItem?> TryContainsFallbackAsync(
        string searchTerm, string musician, Guid[]? topParentIds, Entities.User? user,
        string retryLabel, CancellationToken cancellationToken)
    {
        return await TrySearchFallbackAsync(
            q => q.NameContains = searchTerm, musician, topParentIds, user, retryLabel, applyContainmentBand: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a configured InternalItemsQuery and fuzzy-matches the results against the artist name.
    /// Uses pre-resolved topParentIds to avoid repeated library filter resolution.
    /// </summary>
    private async Task<BaseItem?> TrySearchFallbackAsync(
        Action<InternalItemsQuery> configure,
        string musician,
        Guid[]? topParentIds,
        Entities.User? user,
        string retryLabel,
        bool applyContainmentBand = false,
        CancellationToken cancellationToken = default)
    {
        var query = new InternalItemsQuery()
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            TopParentIds = topParentIds!,
            DtoOptions = new DtoOptions(true)
        };
        configure(query);

        IReadOnlyList<BaseItem> results = await RetryAsync(
            () => _libraryManager.GetItemList(query),
            retryLabel,
            cancellationToken).ConfigureAwait(false);

        if (results == null || results.Count == 0)
        {
            return null;
        }

        // JF-381 gate before fuzzy ONLY for substring-shaped sources (NameContains):
        // a purely coincidental candidate set would be confirmed by the fuzzy step.
        // Prefix-shaped callers (NameStartsWith) pass applyContainmentBand=false: a short
        // query at the START of a long name is the intended ASR-truncation shape
        // ("crash" -> "Crash Test Dummies"), not a coincidence (code-review 2026-08-29).
        var candidates = applyContainmentBand ? FilterContainmentBand(results, musician) : results;
        return FuzzyMatch(musician, candidates, a => a.Name, user);
    }

    /// <summary>
    /// Filters raw search results to the JF-381 containment band (shared predicate with
    /// <see cref="Util.ArtistSearch"/>); used on the database-tier results, which unlike
    /// the in-memory path have no later phonetic-over-full-index tier to self-correct.
    /// </summary>
    /// <param name="artists">Raw candidate artists.</param>
    /// <param name="musician">The raw query.</param>
    /// <returns>The filtered list.</returns>
    private static List<BaseItem> FilterContainmentBand(IReadOnlyList<BaseItem> artists, string musician)
        => artists.Where(a => Util.ArtistSearch.PassesContainmentBand(a.Name, musician)).ToList();
}

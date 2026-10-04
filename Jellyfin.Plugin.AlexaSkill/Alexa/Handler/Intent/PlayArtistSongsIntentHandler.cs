using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
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
        // JF-659: the ER canonical feeds the search; the raw value keeps driving
        // the not-found speech (the JF-642 F-1 lesson; SlotValueHelper owns the
        // full contract).
        string? canonicalMusician = Util.SlotValueHelper.GetCanonicalValue(intentRequest, "musician");
        // JF-426: strip a leading Italian article Amazon failed to strip for an
        // out-of-catalog artist ("suona i 24 grana" arrived raw as 'i 24 grana').
        musician = musician is null ? null : Util.ArtistSearch.StripLeadingArticle(musician, locale);
        // JF-643: the search chain romanizes at its own entry (ArtistSearch.SearchAsync,
        // the ONE query-side choke point); this handler romanizes too because its own
        // downstream surfaces need the Latin value (the not-found speech, the JF-652
        // bar, the JF-654 song fallback). SearchAsync's entry romanization is
        // idempotent for the already-Latin result, so the search adds no cost.
        // JF-652: the kana-origin flag must be captured on the RAW slot value,
        // BEFORE the romanization below erases the script evidence. JF-659: a
        // canonical-bearing query keeps the flag false (IsKanaOriginQuery owns the
        // invariant), so the kana bar is inert for it.
        bool kanaOrigin = Util.ArtistSearch.IsKanaOriginQuery(canonicalMusician, musician);
        musician = musician is null ? null : Util.KatakanaRomanizer.Romanize(musician);

        Logger.LogDebug("PlayArtistSongs: entered, locale={Locale}", locale);

        // JF-550 (dead-mic sweep; JF-549 class).
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "PlayArtistSongs") is { } elicitCancel)
        {
            return elicitCancel;
        }

        if (string.IsNullOrWhiteSpace(musician))
        {
            return BuildDialogElicitResponse("DidNotCatchArtistName", locale, "musician", IntentNames.PlayArtistSongs, Util.ElicitSlots.For(IntentNames.PlayArtistSongs));
        }

        // JF-659: the search/gate path below reads the canonical when present, the
        // romanized raw otherwise; `musician` itself stays the speech value.
        string musicianQuery = canonicalMusician ?? musician;

        // JF-467: primary-path music gate (shared contract on IfMediaTypeDisabled).
        // Placed AFTER the empty-musician prompt and BEFORE the warming gate, so a
        // disabled request never waits on index warming and issues no library query
        // (found ungated by the final review pass; same class as the other four).
        SkillResponse? musicDisabled = IfMediaTypeDisabled(c => c.MusicEnabled, request);
        if (musicDisabled != null)
        {
            return musicDisabled;
        }

        // Layer-1 warming gate (the standard gated-handler placement, JF-419):
        // AFTER slot validation (a slot-less utterance still gets DidNotCatch)
        // and BEFORE the "searching" progressive response, so a warming refusal
        // never speaks the announcement first. The shared chain re-checks at its
        // entry (layer 2, the SearchAsync choke point), as for every other gated
        // handler.
        GuardIndexReady(_artistIndex);

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        SearchResponseMode mode = Search.GetSearchResponseMode(user);

        // JF-448 (review F2), caller-pins-first KEPT (JF-742 code-review F1): this
        // handler pins its own GUARDED view and hands it to the artist composite
        // (whose internal Pin is idempotent on it; the outcome returns the SAME
        // view) because its post-search consumers are sensitive to the
        // null-vs-not-ready-view distinction the composite's unguarded pin would
        // erase on a DISABLED index: Fast mode's best pick passes the view to
        // Search.FuzzyMatchPhonetic, whose null branch runs the PLAIN overload
        // (first ContainmentScore hit wins) while a not-ready view runs the
        // phonetic overload (full scan for a winner above the phonetic floor), so
        // the two can pick DIFFERENT artists among multi DB hits. GuardIndexReady
        // above already threw on warming, so the guard converts only the disabled
        // case to null, exactly the pre-JF-742 behavior.
        // JF-690: the shared multi-value-ER gate (it owns the full contract) sits
        // inside the composite BEFORE the search chain, so a REAL ambiguity's ask
        // never pays for the search (only the earlier "searching" announcement is
        // spoken) and a collapse to one library artist (stale catalog candidates)
        // plays that proven survivor, skipping the search chain that would re-query
        // the stale rank-#1 canonical. JF-734 (the seventh pool site, completing
        // JF-715's six): the zero-resolve fall-through leg seeds the chain with the
        // gate's pool (the preloadedPool contract holds by construction inside the
        // composite: the SAME pinned view feeds gate and chain, Pin is idempotent,
        // and both scope resolutions go through the cached ResolveForUser for the
        // same user). JF-658: the ONE shared chain (ArtistSearch.SearchAsync) is
        // driven with this handler's policy axes: Fast skips the recall tiers and
        // the DB containment band, Thorough runs the parallel DB tiers (the inline
        // chain's Task.WhenAll structure, kept verbatim per the JF-315 6b plan) and
        // the ASR compound-word variants on tier 1. Observability deltas accepted
        // with that fold (plan step 5): the per-tier retry labels collapse to the
        // one "GetArtists" label, including the JF-457 album-scope verification
        // queries the inline path labeled "ArtistAlbumScope" (scope-verify vs
        // search triage now needs the query shape, not the label); SearchAsync's
        // TOTAL log line carries the mode.
        IArtistIndex? pinnedView = _artistIndex?.IsReady == true ? _artistIndex.Pin() : null;
        var gate = await MultiValueErDisambiguation.TryArbitrateOrSearchArtistsAsync(
            intentRequest, user, pinnedView, _libraryManager, Logger, locale, musicianQuery,
            arbitrate: true,
            mode: mode,
            asrCompoundWordFixEnabled: _config.AsrCompoundWordFixEnabled,
            parallelDbTiers: mode != SearchResponseMode.Fast,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (gate.Ask != null)
        {
            return gate.Ask;
        }

        IReadOnlyList<BaseItem> artists = gate.Artists;
        IArtistIndex? pinnedIndex = gate.PinnedIndex;

        // Re-resolved here for the post-search consumers (cached, so no extra
        // library walk): the JF-420/JF-652 pools fetch from the pinned index
        // scoped to the user's libraries, and the artist-songs query reuses the
        // same scope. The handler's own gates fetch the pool at most once per
        // request and share it (JF-658). JF-702 closes the pool-sharing seam the
        // JF-690 marker flagged: on a multi-value ER leg the gate already
        // materialized the scoped pool, so its Pool seeds this cache and the
        // JF-420/JF-652 gates consume that fetch instead of re-materializing
        // (null on every closed-gate leg, byte-identical to before). Known seam
        // (accepted, JF-658 and JF-702; WIDENED by JF-734, which routes the
        // search itself through the gate's fetch): a library-scope cache
        // invalidation landing between the gate's scope resolution and the
        // search serves the search and the gates the gate's earlier scope; the
        // window is milliseconds. Pre-JF-734 the search re-resolved the scope
        // itself, so a stale scope could only reach the prompt shapes below
        // (plus the JF-420 auto-select, which always read the gate's pool);
        // with the search on the gate's pool, ANY tier accept can auto-play a
        // just-removed library's artist unprompted. Same JF-457 name-only leak
        // class, one leg wider.
        Guid[]? topParentIds = Util.LibraryFilter.ResolveForUser(user, _libraryManager, Logger);
        IReadOnlyList<BaseItem>? artistPool = gate.Pool;

        if (artists.Count == 0)
        {
            Logger.LogDebug("PlayArtistSongs: no artist found for query='{Query}'", musicianQuery);

            // JF-439 inverse cross-media fallback: the NLU coin-flips musician-shaped
            // song titles ("suona la canzone sugar free jazz" -> musician="sugar free
            // jazz") into this intent when the RepeatSingle collision region resolves
            // to the artist reading. Before giving up, try the song index and serve
            // the song with a FoundSongInstead announcement. Returns null when the
            // fallback does not apply (guard/miss/warming), leaving the clean
            // NotFoundArtist below. JF-659 (gate review, finding 2): skipped when
            // the slot is ER-resolved: an ER match is artist evidence, so guessing
            // the resolved name as a song TITLE could play an unrelated song titled
            // like the artist (pre-change the romanized raw missed and the honest
            // not-found answered).
            if (canonicalMusician == null)
            {
                SkillResponse? songFallback = CrossMedia.TrySongFallback(
                    musicianQuery, user, session, context, locale, _songNgramIndex, _libraryManager, "PlayArtistSongs", cancellationToken,
                    kanaOrigin: kanaOrigin);
                if (songFallback != null)
                {
                    return songFallback;
                }
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
            && ArtistSearch.IsCoincidentalContainmentMatch(musicianQuery, artists[0].Name, locale))
        {
            return AskCoincidentalContainment(artists[0], musicianQuery, locale, context, user);
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
            && musicianQuery.Contains(' ')
            && musicianQuery.Contains(artists[0].Name, StringComparison.OrdinalIgnoreCase)
            && !ArtistSearch.IsExactNameMatch(musicianQuery, artists[0].Name)
            && pinnedIndex != null)
        {
            // Same pinned view the chain above used (JF-448): the alternative pool and
            // the match list stay on one publish. Fetched lazily here (the JF-658 fold):
            // only gate-firing requests pay the one extra GetArtists.
            var searchPool = artistPool ??= pinnedIndex.GetArtists(topParentIds);
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
                    int raw = FuzzyMatcher.Score(musicianQuery, candidate.Name);
                    if (raw < AlternativeFullNameThreshold)
                    {
                        continue;
                    }

                    double fair = FairComparisonScore(candidate.Name, musicianQuery, raw);
                    if (fair > bestAlternativeFair)
                    {
                        bestAlternativeFair = fair;
                        bestAlternative = candidate;
                    }
                }

                if (bestAlternative != null
                    && !IsRedundantShorterForm(artists[0].Name, bestAlternative.Name, musicianQuery))
                {
                    // JF-420/JF-420.3 SYMMETRIC fair comparison (FairComparisonScore:
                    // bidirectional length fraction, no matcher recall floor). The
                    // alternative must ALSO keep a genuinely high fair score: one that
                    // survives only via the containment exemption (a partial-word hit
                    // like "Miles" inside "miles davis live") cannot outrank a better
                    // full match. If the alternative wins by a clear margin, auto-select
                    // it ("P!nk floyd" means Pink Floyd, not P!nk); otherwise offer both.
                    double containmentFair = FairComparisonScore(artists[0].Name, musicianQuery, FuzzyMatcher.ContainmentScore);

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
                        return DisambiguationHelper.AskMultipleArtists(matchInfos, locale);
                    }
                }
            }
        }

        // Disambiguation: in Fast mode, auto-play the best match
        bool fastAutoPlay = mode == SearchResponseMode.Fast && artists.Count > 1;

        if (artists.Count > 1 && !fastAutoPlay)
        {
            Logger.LogDebug("PlayArtistSongs: {Count} artists matched, running disambiguation", artists.Count);
            var (missOutcome, missResponse) = await HandleFuzzyMiss(
                musicianQuery,
                artists,
                a => a.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeArtist,
                locale,
                best =>
                {
                    artists = new List<BaseItem> { best };
                    return Task.FromResult<SkillResponse>(null!);
                },
                user: user).ConfigureAwait(false);

            if (missOutcome == FuzzyMissOutcome.NotFound)
            {
                Logger.LogDebug("PlayArtistSongs: fuzzy miss outcome=NotFound, asking user to disambiguate");
                var matches = artists.Take(3).Select(a => (a.Id, a.Name, (string?)Launch.GetImageUrl(a.Id.ToString("N"), user))).ToList();
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
            var best = Search.FuzzyMatchPhonetic(musicianQuery, artists, a => a.Name, a => a.Id, pinnedIndex, user);
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

        // JF-382: this single gate covers the two surfaces the JF-377 entry gate above
        // cannot see, because the final single pick is made AFTER it: (1) Fast mode's
        // fastAutoPlay best pick and (2) Thorough count>1's HandleFuzzyMiss auto-accept
        // (score >= ContainmentScore). Same downgrade as JF-377 (AskFirstMatch, never
        // reject): the shape is string-indistinguishable from a real artist inside a
        // carrier phrase, so the yes/no prompt is the only no-regression behavior.
        if (artists.Count == 1
            && ArtistSearch.IsCoincidentalContainmentMatch(musicianQuery, artists[0].Name, locale))
        {
            return AskCoincidentalContainment(artists[0], musicianQuery, locale, context, user);
        }

        // JF-652: the kana-origin auto-play bar. A katakana query romanizes into a
        // romaji class whose score distribution the Latin-calibrated acceptance
        // machinery was never calibrated for (live: 'クイーン' -> 'kuin' silently
        // resolved a 91-tie to Keane; 'ビートルズ' -> 'bitoruzu' plain-fuzzy-accepted
        // 'Sator' at 60). At this single end-of-chain point every collapse path
        // (tier single-best, Fast-mode best pick, HandleFuzzyMiss auto-accept) has
        // already reduced to one pick, so the bar composes WITH the JF-377/JF-420
        // gates above instead of replacing them: a kana-origin query auto-plays only
        // on a real Double Metaphone collision (the phonetic floor), a plain-fuzzy-only
        // pick is the honest not-found (never a confirm prompt), and a floor-level
        // near-tie with a rival fires the existing multi-artist disambiguation
        // (JF-420.2 yes/no cycling). Latin queries never enter this block.
        if (kanaOrigin && artists.Count > 0)
        {
            artistPool ??= pinnedIndex?.GetArtists(topParentIds);
            SkillResponse? kanaOutcome = ApplyKanaOriginAcceptance(
                artists, musician!, user, pinnedIndex, artistPool, locale, context, session, kanaOrigin, cancellationToken);
            if (kanaOutcome != null)
            {
                return kanaOutcome;
            }
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
            OrderBy = CrossMediaFallback.PopularitySort,
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
        var (artistsItems, startIndex, _) = ResumeMath.SortAndFindResumeIndex(
            artistItems, jellyfinUser!, _userDataManager, resumePosition: false);

        if (startIndex > 0)
        {
            Logger.LogInformation(
                "PlayArtistSongs: resuming queue from track {Index} ({Name})",
                startIndex, artistsItems[startIndex].Name);
        }

        if (_config.ShuffleArtistSongs)
        {
            artistsItems = Shuffler.ShuffleCopy(artistsItems);
            startIndex = 0;
            Logger.LogDebug("PlayArtistSongs: shuffled {Count} tracks", artistsItems.Count);
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = startIndex; i < artistsItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = artistsItems[i].Id });
        }

        string itemId = artistsItems[startIndex].Id.ToString();

        Logger.LogDebug(
            "PlayArtistSongs: returning AudioPlayer, itemId={ItemId}, startIndex={StartIndex}, queueSize={QueueSize}, offset=0",
            itemId, startIndex, queueItems.Count);

        // JF-699 item 5: launch build BEFORE any queue/session/continuation write
        // (the ordering policy lives on EnsureStreamTokenDeliverable; a seek-mode
        // refusal must not leave phantom state behind).
        SkillResponse response = Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId, artistsItems[0], user, context, announceLocale: locale);

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
                    SortOrder = CrossMediaFallback.PopularitySort,
                    Shuffle = _config.ShuffleArtistSongs,
                    // JF-674: identity = the queue page just installed (see MintedQueueItemIds).
                    MintedQueueItemIds = QueueContinuation.QueueIdsOf(queueItems)
                });
        }

        return response;
    }

    /// <summary>
    /// JF-652: the kana-origin acceptance decision for the final single pick. Returns
    /// the response to return (the honest not-found, or the near-tie disambiguation
    /// ask), or null when the pick clears the kana bar and auto-play proceeds.
    /// The bar (one shared definition, ArtistSearch.PassesKanaOriginAcceptance in
    /// the Util collaborator): the user's
    /// threshold AND a REAL Double Metaphone code collision between the romanized
    /// query and the candidate, checked on the codes (index pre-computed, or encoded
    /// here from the candidate name), never inferred from the score band. The
    /// near-tie check runs the shared
    /// <see cref="Util.ArtistSearch.FindNearTiedRunnerUp"/> over the full artist
    /// pool (the request-memoized pool, fetched lazily from the pinned index and
    /// shared with the JF-420 gate; only the in-memory path has one, the
    /// cold-index database path skips it, same cold-window trade-off class as
    /// the JF-381/JF-417 gates).
    /// </summary>
    private SkillResponse? ApplyKanaOriginAcceptance(
        IReadOnlyList<BaseItem> artists,
        string musician,
        Entities.User user,
        IArtistIndex? pinnedIndex,
        IReadOnlyList<BaseItem>? artistPool,
        string locale,
        Context context,
        SessionInfo session,
        bool kanaOrigin,
        CancellationToken cancellationToken)
    {
        BaseItem match = artists[0];
        int score = Util.ArtistSearch.ScoreBestWithCodes(musician, new[] { match }, pinnedIndex)?.Score ?? 0;
        int userThreshold = FuzzyMatcher.GetDefaultThreshold(user);

        if (!Util.ArtistSearch.PassesKanaOriginAcceptance(musician, match, score, userThreshold, pinnedIndex))
        {
            // Name the actual failing leg: the bar is (threshold AND code collision),
            // and the two misses mean different things at triage (review round 2,
            // finding 6).
            string missReason = score < userThreshold
                ? $"below the user's threshold ({userThreshold})"
                : "without a Double Metaphone code collision";
            Logger.LogInformation(
                "PlayArtistSongs: kana-origin query '{Query}' matched '{Match}' at score {Score} {Reason}, downgrading to not-found (JF-652)",
                musician, match.Name, score, missReason);
            // JF-654 review round 2: the flag rides the method param (the kana guard
            // at the single-pick end gate passes it), so a future non-kana caller
            // inherits false instead of a silent song bar on Latin queries. The
            // value is also PINNED explicitly: `musician` here is already
            // romanized (the handler's entry romanization), so TrySongFallback's
            // self-computation would see Latin and leave the bar inert.
            SkillResponse? songFallback = CrossMedia.TrySongFallback(
                musician, user, session, context, locale, _songNgramIndex, _libraryManager, "PlayArtistSongs", cancellationToken,
                kanaOrigin: kanaOrigin);
            if (songFallback != null)
            {
                return songFallback;
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundArtist", locale, musician));
        }

        if (artistPool != null)
        {
            var pair = Util.ArtistSearch.FindNearTiedRunnerUp(musician, match, score, artistPool, pinnedIndex, userThreshold);
            if (pair != null)
            {
                Logger.LogInformation(
                    "PlayArtistSongs: kana-origin query '{Query}' is a near-tie between '{Top}' ({TopScore}) and '{RunnerUp}' ({RunnerScore}), disambiguating (JF-652)",
                    musician, pair.Value.First.Name, pair.Value.FirstScore, pair.Value.Second.Name, pair.Value.SecondScore);
                var matchInfos = new List<DisambiguationHelper.MatchInfo>
                {
                    new() { Id = pair.Value.First.Id.ToString(), Name = pair.Value.First.Name },
                    new() { Id = pair.Value.Second.Id.ToString(), Name = pair.Value.Second.Name }
                };
                return DisambiguationHelper.AskMultipleArtists(matchInfos, locale);
            }
        }

        // Clear margin (or no pool on the database path): the winner auto-plays.
        return null;
    }

    /// <summary>
    /// The JF-377/JF-382 downgrade response: a single-candidate yes/no ask for an
    /// artist that matched only as a coincidental containment inside the query text.
    /// Shared by the entry gate and the final-pick end-gate so the ask construction
    /// (art lookup, attribute shape) lives once.
    /// </summary>
    private SkillResponse AskCoincidentalContainment(BaseItem artist, string musician, string locale, Context context, Entities.User user)
    {
        Logger.LogInformation(
            "PlayArtistSongs: match='{Match}' for query='{Query}' is coincidental-containment, downgrading to disambiguation",
            artist.Name, musician);
        var matches = new List<(Guid Id, string Name, string? ArtUrl)>
        {
            (artist.Id, artist.Name, Launch.GetImageUrl(artist.Id.ToString("N"), user))
        };
        return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeArtist, locale, context);
    }
}

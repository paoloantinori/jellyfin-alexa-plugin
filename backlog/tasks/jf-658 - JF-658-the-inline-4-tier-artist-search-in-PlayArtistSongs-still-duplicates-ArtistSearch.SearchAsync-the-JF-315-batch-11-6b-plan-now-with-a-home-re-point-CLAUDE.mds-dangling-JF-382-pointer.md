---
id: JF-658
title: >-
  JF-658 - the inline 4-tier artist search in PlayArtistSongs still duplicates
  ArtistSearch.SearchAsync (the JF-315 batch-11 '6b' plan, now with a home);
  re-point CLAUDE.md's dangling JF-382 pointer
status: To Do
assignee: []
created_date: '2026-09-27 19:12'
labels:
  - refactor
  - tech-debt
  - search
dependencies: []
references:
  - >-
    backlog/tasks/jf-315 -
    Refactor-Decompose-the-2268-line-BaseHandler-God-class-into-injected-collaborators.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the backlog audit (JF-315 closed; this task is the home for the one plan that had none).

CONTEXT: PlayArtistSongsIntentHandler still carries its own inline 4-tier artist search (Fast/Thorough/Parallel mode selection), duplicating ArtistSearch.SearchAsync. The consolidation was planned twice inside JF-315's batch-11 notes (stopped twice at the batch boundaries); the full plan and the stopping history live in JF-315's notes (the '6b' section). The artist-SONGS query blocks are long consolidated into SearchService.GetArtistSongsAsync.

THE WORK: fold the inline 4-tier chain into ArtistSearch.SearchAsync per the 6b plan (the plan table in JF-315's notes is the work order; re-derive anything stale against the current tree). Retire the JF-643 romanization site at the inline chain's entry with it (ArtistSearch.SearchAsync's entry already romanizes; the JF-382 scope note in that task records the same).

ALSO in this edit: CLAUDE.md's Artist Search Fallback Chain section still says 'consolidate via JF-382', which now dangles (JF-382 was the coincidental-containment task, Done, and never held the inline-search plan); re-point it here.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
EXECUTED 2026-09-29 per the JF-315 batch-11 6b plan, re-derived against the current tree (the kana machinery JF-652/654/659/660 and the JF-643 romanization landed after the plan was written; line anchors moved).

THE FOLD MAP (every piece of the inline chain and where it went):
- Signature: ArtistSearch.SearchAsync gained three OPTIONAL caller-policy params, all defaulting to the pre-fold shared behavior so the 10 other call sites compile untouched and keep Thorough-sequential-no-ASR: SearchResponseMode mode = Thorough, bool asrCompoundWordFixEnabled = false, bool parallelDbTiers = false. The params are documented as POLICY axes, not judgments (the JF-408 letter; SearchAsync still owns only recall).
- In-memory Fast: tier 1 (Contains + JF-381 band, unchanged) then straight to tier-4 fuzzy-all; skips tiers 2/3/1.5 (plan step 3; the tier-4 log keeps the mode=Fast suffix).
- In-memory Thorough: tiers 2 (+JF-417 deferral), 3, 1.5 (JF-437), 4, deferred fallback; today's shared body verbatim modulo the re-nesting (tier locals renamed tier3Fuzzy/tier4Fuzzy for the merged scope).
- DB Fast: single SearchTerm query (no ASR variants, the flag folds mode into the boolean), NO JF-381 band (the documented Fast exception, rationale now owned once at the tier-1 gate), JF-457 album-scope filter, no fallback tiers.
- DB Thorough: tier 1 through the ONE ASR variant loop (plan step 1: SearchService.SearchWithAsrFallbackAsync's core extracted as internal static SearchFirstNonEmptyAsync(query, searchFunc, tryAsrVariants); the public wrapper delegates with its config-flag+mode fold, its 6 wrapper tests untouched and green), then band, album-scope, then tiers 2-4 behind parallelDbTiers: Task.WhenAll over the EXISTING PrefixSearchAsync/ContainsSearchAsync with the 2>3>4 priority pick (structure kept verbatim per plan step 6); parallelDbTiers=false keeps today's sequential shape for every other caller.
- Handler: pins caller-first (`_artistIndex?.IsReady == true ? _artistIndex.Pin() : null`, the TryEntityFallbackAsync precedent; SearchAsync's own Pin is idempotent on it), passes (mode, _config.AsrCompoundWordFixEnabled, mode != Fast); DELETED: the whole inline chain, TryPrefixFallbackAsync, TryContainsFallbackAsync, TrySearchFallbackAsync, FilterAlbumScopeAsync, FilterContainmentBand, the Stopwatch locals, 3 genuinely-unused usings (the 4th, Model.Session, carries QueueItem and stays). The handler keeps: the layer-1 GuardIndexReady (comment re-pointed to the standard layer-1-before-progressive-response rationale; WarmingGateCoverageTests roster unchanged), the mode read (GetSearchResponseMode stays handler-side; the serve path reads it), and every post-search judgment (JF-377 x2, JF-420, fastAutoPlay, disambiguation, JF-652).
- Pool/topParentIds: SearchAsync resolves the scope internally; the handler re-resolves after the search (LibraryFilter.ResolveTopParentIds is static-cached, so GetItemById Times.Once pins stay green) and the JF-420 + JF-652 pools fetch lazily from the pinned index, memoized once per request (`artistPool ??=`), so non-gate paths keep GetArtists Times.Once and gate paths pay at most ONE extra GetArtists (plan row 24's accepted routing cost).
- Grep proof, one search implementation: zero hits for GetArtistsFuzzy/GetArtistsFullPrefix/GetArtistsContains/Try*FallbackAsync/FilterContainmentBand/FilterAlbumScopeAsync in production; PlayArtistSongsIntentHandler constructs no artist query of any shape; BaseItemKind.MusicArtist tier-query construction lives only in ArtistSearch.cs (BuildArtistQuery, the new single preamble owner).

KANA-FAMILY INTERACTIONS (each preserved or dispositioned):
- JF-652 ApplyKanaOriginAcceptance: STAYS the handler's end-of-chain gate, inputs intact (single-pick artists, the romanized raw `musician`, pinnedIndex, the memoized pool, the pinned kanaOrigin flag). Pool non-null iff the in-memory path ran, exactly the pre-fold split.
- JF-659 canonical read: musicianQuery = canonical ?? romanized-raw kept verbatim at the handler. ONE deliberate corner convergence: the canonical now flows through SearchAsync's entry romanization (the ONE query-side choke point), so a KANA canonical loses the inline chain's verbatim exact-self-match and rides the JF-643 accepted narrowing, uniform with the other 7 musician handlers (FindSong/QueryArtistLibrary already behaved this way pre-fold). IsKanaOriginQuery's doc updated to state the real invariant; the sharper mixed-library reachability the code-review round found (romanized kana canonical weakly fuzzy-hitting a Latin artist with the bar inert) is FILED in JF-645's notes this turn (its symmetric-normalization item is the tracked home; zero test pins exist on the kana-canonical shape).
- JF-654 song fallback: both TrySongFallback call sites (the not-found branch with the canonical-null guard, the kana-bar miss leg) untouched, kanaOrigin pinned explicitly on both.
- JF-381/JF-417/JF-420 gates: all four now live ONCE (on SearchAsync and the handler gate respectively); the Fast-mode DB band exception is preserved and its rationale consolidated at the tier-1 gate.
- JF-643 romanization: the handler's entry romanization STAYS (its own downstream needs the Latin value: not-found speech, the JF-652 bar, the JF-654 fallback); the comment re-pointed from "the inline chain bypasses SearchAsync" to the idempotent-choke-point rationale.

TESTS (plan steps 0+6): 4 characterization pins written FIRST, green on the pre-fold inline chain (new file PlayArtistSongsSearchModeAxisTests: Fast in-memory tier-skip via the JF-437 beatles-live shape playing Eagles, Fast DB single ungated query playing Florence + The Machine, parallel tiers issuing NameContains despite the tier-2 hit with 2>3>4 priority, Thorough DB tier-1 ASR variant lazybones), all 4 green post-fold unchanged; 4 shared-layer facts added to ArtistSearchTests (Fast in-memory skip with the Thorough control, Fast DB single query, parallel all-issued + priority, ASR variants on/off); TestHelpers.CreateTestUser grew the searchResponseMode param (the factory convention) and the pins reuse GetPlayDirective. ZERO edits to pre-existing tests; the ONE mid-fold failure (ProgressiveQueueTests full-prefix fallback) was a design violation fixed in production, not a fixture edit: the shared PrefixSearchAsync/ContainsSearchAsync lacked the deleted inline helper's null-result tolerance, restored with `?? Array.Empty<BaseItem>()` (production GetItemList never returns null; behavior identical).

OBSERVABILITY DELTAS (plan step 5, all named in the code where they bite): per-tier retry labels collapse to the one "GetArtists" label INCLUDING the album-scope verification queries the inline path labeled "ArtistAlbumScope"; the handler-side total line is gone but SearchAsync's total line GAINED mode={Mode}; three FuzzyMatchPhonetic LogDebug lines lost on the in-memory tiers (the private FuzzyMatch does not log). KNOWN SEAM accepted and documented at the re-resolution site: a library-scope cache invalidation between SearchAsync's internal resolution and the handler's re-resolution would serve the gates a fresher scope than the search ran under (milliseconds window, name-only leak class).

GATES + VERIFICATION TAIL: /simplify 4-angle pass, findings applied (DB tier-1 Fast/Thorough copy-paste collapsed behind a single mode-conditioned band + shared tail; BuildArtistQuery + FirstWordOf locals own the query preamble and first-word idioms; mode added to the total log; the WhenAll claim wording corrected to the accurate statement the sync-channel shape supports, retry backoffs overlap while the queries share the request thread, an INHERITED shape kept verbatim per the plan; pool fetch memoized once per request; test helpers reused; doc-count fix 11 to 10; skipped with reasons: the Task.Run execution-model change (behavior change, out of a no-behavior-change fold) and the Mock-vs-FakeArtistIndex swap (sibling-suite convention)). code-review high: fold verified mechanically faithful, 4 findings dispositioned (F1 kana-canonical reachability FILED in JF-645 same-turn; F2 scope seam documented at the site; F3 label collapse named precisely in the comment; F4 comment overstatement corrected). Builds: Debug + Release solution, 0 errors 0 warnings both TFMs. Suites: 4727/4727 BOTH TFMs on the post-simplify-fixes state AND on the final state (4719 baseline + 4 pins + 4 shared facts, zero expectation edits).
<!-- SECTION:NOTES:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

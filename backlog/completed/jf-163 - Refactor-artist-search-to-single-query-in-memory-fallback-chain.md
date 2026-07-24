---
id: JF-163
title: Refactor artist search to single-query + in-memory fallback chain
status: Done
assignee:
  - claude
created_date: '2026-05-17 05:35'
updated_date: '2026-05-17 06:21'
labels:
  - performance
  - search
  - artist
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/RetryHelper.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Problem**: `PlayArtistSongsIntentHandler` runs up to 4 sequential EF Core → SQLite queries per artist request (SearchTerm → first-word prefix → full prefix → NameContains). Each query is wrapped in `RetryAsync` (3 retries, exponential backoff, 6s budget). While individual queries are fast (~10ms for typical libraries), the cumulative cost and retry potential add latency to a voice interaction path that should feel instant.

**Current state** (JF-160 follow-up):
- Tier 1: `SearchTerm` — `CleanName.Contains()` on DB (no index)
- Tier 2: `NameStartsWith` first word — `SortName.StartsWith()` on DB (no dedicated index)
- Tier 3: `NameStartsWith` full query — same as tier 2
- Tier 4: `NameContains` full query — `CleanName.Contains()` on DB (no index)

**Proposed approach**: Query all artists for the user once (with `IncludeItemTypes = MusicArtist` + `ApplyLibraryFilter`), then run all 4 matching strategies in-memory against that single result set.

**Performance measurement requirement**:
Before refactoring, add `Stopwatch` instrumentation to the current fallback chain to log:
- Time per tier (ms)
- Which tier matched
- Total time for the full chain
- Result count from each DB query

Run this in production for a few days with real utterances to collect baseline data. After refactoring, compare same metrics.

**Acceptance criteria**:
- [ ] Baseline perf data collected (log format: `ArtistSearch: tier={N} duration={ms}ms results={count} matched={bool}`)
- [ ] Single `GetItemList` call fetches all artists for user (with library filter)
- [ ] All 4 matching strategies (SearchTerm equivalent, prefix-first-word, prefix-full, contains) run in-memory
- [ ] Stopwatch timing logs added for the new approach
- [ ] A/B comparison data logged to inform keep/revert decision
- [ ] All existing tests pass
- [ ] New test for in-memory fallback chain

**Modified files**: `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs`
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Baseline perf data collected with tier timing logs
- [ ] #2 Single GetItemList call fetches all artists for user (with library filter)
- [ ] #3 All 4 matching strategies run in-memory
- [ ] #4 Stopwatch timing logs added for new approach
- [ ] #5 All existing tests pass (1502/1502)
- [ ] #6 New unit tests for in-memory fallback chain (16 tests)
- [ ] #7 E2E test added: TestPlayArtistSongsInMemoryChain
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan

### Phase 1: Add Stopwatch Instrumentation (baseline)
- Add `System.Diagnostics.Stopwatch` to `HandleAsync` in `PlayArtistSongsIntentHandler.cs`
- Log per-tier timing: `ArtistSearch: tier={N} duration={ms}ms results={count} matched={bool}`
- Log total chain time
- This establishes baseline metrics before refactoring

### Phase 2: Refactor to Single Query + In-Memory Fallback
- Replace 4 separate `GetItemList` calls with a single call: `IncludeItemTypes = MusicArtist` + `ApplyLibraryFilter`, no SearchTerm/NameStartsWith/NameContains filters
- Add a private method `FindArtistInMemory(query, allArtists, user)` that implements all 4 matching tiers:
  1. FuzzyMatch on full list (equivalent to SearchTerm)
  2. Filter by `Name.StartsWith(firstWord)` then FuzzyMatch
  3. Filter by `Name.StartsWith(fullQuery)` then FuzzyMatch
  4. Filter by `Name.Contains(query)` then FuzzyMatch
- Keep Stopwatch timing for A/B comparison
- Keep `TryPrefixFallbackAsync`/`TryContainsFallbackAsync` as dead code (can remove later)

### Phase 3: Unit Tests
- Create `PlayArtistSongsIntentHandlerTests.cs` with tests for:
  - Exact match returns immediately
  - Truncated name falls back to prefix match
  - Multi-word artist falls back to full-prefix match
  - Substring match catches "The Kidz Bop Kids" from "Kidz Bop"
  - No match returns NotFoundArtist response
  - Multiple results triggers disambiguation
  - Stopwatch timing logs emitted

### Phase 4: Simplify + Verify
- Run simplify agent on changed code
- `dotnet build` and `dotnet test` — all 1487+ tests pass

### Phase 5: E2E Test
- Add E2E test case for artist search to `tests/integration/fixtures/e2e_it-IT.yaml`
- Verify via `test_simulator.py` pattern
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
## Implementation Summary

Refactored `PlayArtistSongsIntentHandler` from 4 sequential DB queries to a single `GetItemList` call + 4-tier in-memory fallback chain.

### What changed:
- Replaced 4 separate `GetItemList(SearchTerm/NameStartsWith/NameContains)` calls with one `GetAllArtists` query
- All 4 matching tiers now run in-memory: FuzzyAll → PrefixFirstWord → PrefixFull → Contains
- Added `Stopwatch` instrumentation logging per-tier timing and total duration
- Added `SafeStartsWith`/`SafeContains` helpers to guard against null SortName access
- Removed `TryPrefixFallbackAsync`, `TryContainsFallbackAsync`, `TrySearchFallbackAsync` methods

### Files modified:
- `PlayArtistSongsIntentHandler.cs` — complete refactor of artist search
- `ProgressiveQueueTests.cs` — updated mock to match single-query pattern
- `test_simulator.py` — added `TestPlayArtistSongsInMemoryChain` E2E test class

### New files:
- `PlayArtistSongsIntentHandlerTests.cs` — 16 unit tests covering all tiers

### Test results:
- 1502 tests passed, 0 failed, build 0 errors/0 warnings

## A/B Performance Comparison (reverted)

Measured against a live library with 1133 artists on minix:

| Query | Baseline (4 DB queries) | Refactored (1 DB + in-memory) |
|-------|------------------------|-------------------------------|
| Queen (warm) | 7ms | 273ms |
| Soul Coughing (exact) | 105ms | 163ms |
| soul coughin (truncated) | 7ms | 137ms |
| Maneskin (multi-tier) | 105ms | 900ms |
| xyznonexistent (miss) | 9ms | 157ms |

**Conclusion**: Single-query approach is a net regression for libraries with 1000+ artists. The full-table fetch (130-270ms) dominates cost.

**Action taken**: Reverted handler to original 4-DB-query approach with Stopwatch instrumentation added for ongoing monitoring. Created follow-up task JF-164 to build a pre-loaded in-memory artist index with event-driven refresh, which would eliminate the fetch cost and make in-memory matching strictly faster.

**What was kept**:
- Stopwatch per-tier timing logs on the original handler (useful for ongoing monitoring)
- Backlog task JF-164 for the in-memory index approach

**What was reverted**:
- Handler back to original DB-based fallback chain
- ProgressiveQueueTests back to original mock pattern
- PlayArtistSongsIntentHandlerTests.cs removed (tested FindArtistInMemory which no longer exists)
- test_simulator.py TestPlayArtistSongsInMemoryChain removed
<!-- SECTION:FINAL_SUMMARY:END -->

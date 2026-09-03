---
id: JF-448
title: >-
  JF-431/432 follow-ups: cross-snapshot artist window, generic snapshot base,
  freezing, 4-copy predicate, test/table/local hygiene
status: Done
assignee: []
created_date: '2026-09-02 01:46'
updated_date: '2026-09-03 03:17'
labels:
  - code-review
  - follow-up-family
  - index
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Util/ArtistSearch.cs:250'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Util/DebouncedLibraryIndexService.cs:76'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs:351'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-ups from the JF-431/JF-432 code-review round (2026-09-02). Of the 9 findings: the 2 VideoAudioCache defects (the UnauthorizedAccessException wedge - one root-owned cache entry permanently failing all uncached plays with a pin leak - and the tripwire-blind-to-IO-failure) were fixed the same night by a follow-up agent; this task tracks the remaining 7: the cross-snapshot artist composition window (the one finding that affects live routing correctness, 1-request window), the base-class generic that would make the snapshot invariant structural, snapshot freezing, the 4-copy containment predicate, the duplicated test pairs, the measurement-table duplication, and the vestigial locals.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 F2 cross-snapshot artist window: GetArtists reads snapshot A but each TryGetPhoneticCode re-reads the LIVE snapshot (ArtistSearch.cs:647 + BaseHandler.cs:1359); a publish in the 1-10ms gap can null the phonetic code, skip the JF-381 floor, and play the wrong artist for one request. Vehicle: the new internal CurrentSnapshot (+ IArtistIndex surface) so the chain captures once
- [ ] #2 F4 base-class ownership: volatile-snapshot-field + Empty + CurrentSnapshot duplicated per service; a generic DebouncedLibraryIndexService<TSnapshot> owning the field + protected Publish makes the invariant structural for future indexes
- [ ] #3 F5 snapshot freezing: records wrap live List/Dictionary behind IReadOnly from LoadAsync locals; freeze at construction or assert init-only in the structural test
- [ ] #4 F6 containment predicate: map.TryGetValue + Array.IndexOf >= 0 exists in 4 byte-identical copies (SongNgram:120/187/239 + ArtistIndex:68); extract to Util/LibraryFilter (HashSet form is the natural perf win)
- [ ] #5 F7 test pairs: the 3 new JF-432 test pairs are near-verbatim copies; shared AssertSingleSnapshotField helper next to PluginTestBase
- [ ] #6 F8 measurement-table duplication: JF-431 numbers live in the inline comment AND the task file; constraint + one-line pointer in code, numbers in one place
- [ ] #7 F9 vestigial locals: 5 of 7 single-use unwrap aliases in Search/SearchPhonetic/SearchBySingleTokens/GetArtists; read snapshot.X at the use site
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All seven ACs resolved. AC#1 (the live-correctness item): IArtistIndex.CaptureSnapshot() returns a read view pinned to one published snapshot (SnapshotView: snapshot + readiness captured, idempotent); ArtistSearch.SearchAsync, BaseHandler.TryEntityFallbackAsync (including its post-chain phonetic confirm - the third compose window, beyond the two the AC cited), and the PlayArtistSongs inline chain (incl. the JF-420 gate and fast auto-play) pin once and resolve artist list AND phonetic codes from the same publish; ArtistIndexExtensions.Pin ([NotNullIfNotNull], ?? index degrade for loose mocks) centralizes the policy; a mid-search-publish chain test proves the pin discriminates (sabotage-verified: without the pin the cup-for-Koop phonetic floor is lost). AC#2: DebouncedLibraryIndexService<TSnapshot> generic companion owns the volatile field + Empty + internal CurrentSnapshot + protected Publish (a subclass cannot write the field except through Publish; JF-432's invariant made mechanical; DI and both ctors unchanged; snapshot records public by CS0060 force but ctors internal). AC#3: construction-time freezing (defensively copied arrays + FrozenDictionary for every map incl. inner per-token lists); pinned by the frozen-against-loader-locals test; the structural one-field test preserved via the shared AssertSingleSnapshotField helper. AC#4: ALREADY DONE by JF-456's FilterByLibraryScope (verified: all four sites call it; credited there). AC#5: shared helper next to PluginTestBase; domain-specific pairs stay concrete. AC#6: measurement table single-homed in the JF-431 task notes, constraint + pointer in code. AC#7: all five cited aliases inlined. Review gate (5 reviewers + scorer): no finding >= 80; five score-50 items applied or recorded (sub-ms pin-before-gate window on TryEntityFallback - conservative direction, first-load instant; JF-420 gate disabled-index-recovery delta; ctor-ordering nuance - unreachable; AC#4 credit; redundant directive removed). Suite 3047/0, Release 0 warnings. Commit 5a8d6e34.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

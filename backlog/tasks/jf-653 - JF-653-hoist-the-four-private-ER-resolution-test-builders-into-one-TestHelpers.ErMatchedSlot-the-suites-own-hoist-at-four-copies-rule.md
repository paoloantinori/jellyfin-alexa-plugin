---
id: JF-653
title: >-
  JF-653 - hoist the four private ER-resolution test builders into one
  TestHelpers.ErMatchedSlot (the suite's own hoist-at-four-copies rule)
status: Done
assignee: []
created_date: '2026-09-27 10:07'
updated_date: '2026-09-28 23:19'
labels:
  - tests
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-642 -
    JF-642-ja-JP-noun-qualified-artist-carriers-stolen-by-PlayByGenre-the-free-text-genre-capture-even-canonical-pre-existing-forms-route-to-genre-artist-intent-unreachable-by-noun-voice.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-642 /simplify round (two reviewers converged on it; the fix touches three test files outside the JF-642 diff, hence a follow-up rather than an in-diff change).

THE FINDING: the test suite now carries FOUR private hand-rolled Alexa.NET Resolution object-graph builders (each ~25 lines of Resolution/Authorities/ResolutionAuthority/ResolutionStatus/ResolutionValueContainer/ResolutionValue nesting, differing only in payload): SetPlaybackSpeedIntentHandlerTests.cs:~78 (RateSlot, Id-keyed), PlayNextEpisodeIntentHandlerTests.cs:~114 (CreatePositionSlot, Id+Name), Unit/PlaybackSpeedTests.cs:~126 (EntitySlot, Id+Name), and PlayByGenreIntentHandlerTests.cs:~373 (CreateIntentRequestWithResolution, Name-only, JF-642). This is the count at which the suite's own house rule (TestHelpers doc comments: TestCandidate 'replaces the four private per-file copies', CreateSong 'hoisted the third private copy', AssertElicitsSlot 'was eight per-file copies') has fired twice before.

THE WORK: one shared builder in TestHelpers, e.g. `TestHelpers.ErMatchedSlot(string name, string? rawValue = null, string? id = null)` returning a Slot with the ER_SUCCESS_MATCH authority graph (the id=null case covers the Name-only shape); migrate all four sites; per-class IntentRequest wrappers stay per-class (each binds different intents/slots). Mechanical, test-only.

VERIFICATION: the four suites stay green byte-identical (no behavior change anywhere); grep shows zero remaining private copies of the authority graph.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DONE 2026-09-29, commit 2784bc85 (worktree branch worktree-agent-a0fdc125eb0d406d2, not pushed). DECISION: MERGE, not compose. The task's sketched TestHelpers.ErMatchedSlot was filed before JF-659 landed TestHelpers.ResolvedSlot; two ER-graph builders in TestHelpers would be one too many, so ResolvedSlot was extended instead: a trailing optional string? id = null threads into ResolutionValue.Id, and the bare-slot trigger stays canonical == null, so every pre-existing ResolvedSlot caller (MusicianErCanonicalTests, SlotValueHelperTests) is untouched and byte-identical. ResolvedSlot's shape carries three of the four sites DIRECTLY (PlayNextEpisode CreatePositionSlot and PlayByGenre's genre slot delegate in one line; Unit/PlaybackSpeedTests EntitySlot keeps a one-line adapter only to state the raw==canonical echo once). SetPlaybackSpeed RateSlot also keeps a one-line adapter because its graph trigger is the id (its authority canonical echoes the raw value), and ResolvedSlot deliberately does NOT make id a graph trigger (no suite mints an id-only match; that contract is now in the ResolvedSlot doc comment). The three adapters contain no authority graph; the PlayByGenre IntentRequest wrapper stays per-class per the task. Proof: four affected classes 115/115 per TFM by filter; full suite 4716/4716 net9.0 + 4716/4716 net10.0, zero assertion edits; grep 'new Resolution|ResolutionAuthority' over the test project hits only TestHelpers.ResolvedSlot itself. -74 net lines. gate-exempt trivial (test-only mechanical hoist).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-29 as merge 4e60fb1f (pushed; test-only, no deploy): the four private ER slot builders hoisted onto TestHelpers.ResolvedSlot. The decision was MERGE not compose (two ER-graph builders in TestHelpers would be one too many): ResolvedSlot gained a trailing optional id parameter threaded into ResolutionValue.Id with the bare-slot trigger unchanged (canonical == null), every pre-existing caller untouched; the three Id-keyed sites became one-line per-class adapters absorbing their trigger/echo differences; the PlayByGenre IntentRequest wrapper stayed per-class per the task. Zero assertion edits; grep shows the authority graph only in TestHelpers itself; suites 4716/4716 both TFMs (worker's four-class 115/115 filter + my independent full run).
<!-- SECTION:FINAL_SUMMARY:END -->

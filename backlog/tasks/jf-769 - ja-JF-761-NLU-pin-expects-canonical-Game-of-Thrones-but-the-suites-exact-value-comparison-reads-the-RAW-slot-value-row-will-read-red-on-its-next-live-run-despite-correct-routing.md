---
id: JF-769
title: >-
  ja JF-761 NLU pin expects canonical "Game of Thrones" but the suite's
  exact-value comparison reads the RAW slot value (row will read red on its next
  live run despite correct routing)
status: Done
assignee: []
created_date: '2026-10-05 00:00'
updated_date: '2026-10-05 03:12'
labels:
  - nlu
  - tests
  - ja-JP
dependencies: []
references:
  - JF-761
  - JF-766
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-766 worktree session (2026-10-05), same-turn as the finding.

The JF-761 regression pin in `tests/integration/fixtures/ja-JP.yaml` (the
`game of thrones のシーズン 1 エピソード 3 を見たい` row) pins
`series_name: {value: "Game of Thrones"}` (canonical committed-type case).
The NLU suite's exact-value comparison (`test_nlu.py`, JF-426 semantics)
compares against `slot.value` from `SmapiClient.parse_profile_nlu_result`,
and profile-nlu returns the SPOKEN RAW TEXT there, not the ER canonical.

Live evidence (2026-10-05, skill discovered fresh, post-ja-rebuild, single
probe, deterministic shape class):
`game of thrones のシーズン 1 エピソード 3 を見たい` → PlayEpisodeIntent
(routing correct, the steal IS closed) with slots series_name value='game of
thrones' (raw, lowercase), season_number='1', episode_number='3'. The pinned
"Game of Thrones" does not equal 'game of thrones', so the row will FAIL the
value assertion on its next live suite run.

The JF-761 close evidence already flagged this as open ("the canonical-value
comparison is the NLU suite's business on its next run"); this filing makes
it tracked rather than latent.

FIX (one line + comment): change the pin value to the raw 'game of thrones'
(the same choice the JF-766 hi-IN pin made deliberately, see its fixture
comment), OR extend the comparison to accept the ER canonical when the raw
matches case-insensitively. The one-line pin change is the smaller fix and
keeps the JF-426 exact-value semantics untouched.

Also worth a look while there: whether any OTHER exact-value pin in the
fixtures pins a canonical-case value for a slot that profile-nlu returns raw
(grep for exact-value pins whose value differs in case from the utterance
text).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 ja pin value corrected (or comparison canonical-aware) with the reason recorded inline (value changed to the raw 'game of thrones'; comparison left untouched: a canonical-aware comparison fixes only this case subclass while per-pin probe-derived values are needed anyway for the surface-substitution subclass, the it-IT P!nk and Duration pins, so exact == stays; the pin comment rewritten because its value claim, canonical surface on ER match, was refuted by the live probe)
- [x] #2 Sweep: no other fixture exact-value pin has the same raw-vs-canonical mismatch (16 exact-value pins audited across the 17 NLU locale fixtures, zero in the e2e fixtures: 8 raw-and-correct (4x es 'breaking bad', en 'rhapsody', it 'rapsodia', it 'ada', hi 'game of thrones'), 7 deliberate non-raw documented and live-verified, a different class (it 'i P!nk floyd' catalog-substitution truth pin live 2026-09-13; 6x it PT30M/PT60M/PT30S built-in AMAZON.Duration ISO normalization from JF-618/JF-622), and the 1 fixed here (ja). The ja season_number/episode_number assertions are presence-only, not exact-value pins. No acronym-bearing exact-value pins exist, so no ambiguous spoken form needed a guess)
- [x] #3 Live NLU suite run for ja-JP green on the corrected row (2026-10-05 live, skill amzn1.ask.skill.33dfacd5 discovered fresh: full ja-JP run 19/19 NLU rows passed including this row; node id test_nlu.py::test_utterance_resolves_correct_intent[ja-JP - game of thrones ...] also confirmed PASSED isolated; the run's only 2 failures are test_e2e smoke two-step rows hitting the documented simulate-skill per-locale open-outage class, unrelated to the fixture)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixture-only fix, no production or model change. The ja JF-761 pin value moved from the canonical 'Game of Thrones' to the raw spoken 'game of thrones' (tests/integration/fixtures/ja-JP.yaml) with the pin comment rewritten to the observed raw-surface rationale; the suite's JF-426 exact-value comparison in test_nlu.py was confirmed raw (slot.value from parse_profile_nlu_result, plain ==) and deliberately left untouched: a canonical-aware comparison fixes only this case subclass, while the surface-substitution subclass (it-IT P!nk catalog substitution, the Duration ISO pins) needs per-pin probe-derived values regardless, so exact == stays. The 16-pin sweep found zero other raw-vs-canonical mismatches; the deliberate non-raw pins are documented, live-verified, and a different class. Green on the live ja-JP suite the same day. Note for future pins: the returned surface is row-specific and must come from a live probe of that row, never from the committed type values; the it-IT P!nk row returned the catalog surface on an ER match while the ja row returned raw on both recorded probes, and JF-761 recorded 'catalog-INDEPENDENT: ER_SUCCESS_MATCH changed nothing' for that title, so the ER mechanism behind the surface is an unresolved conflict, surfaced not averaged; a raw pin can flip red when the live surface changes, and the flip is distinguished from real drift by re-probing.

CLOSED 2026-10-05 by the orchestrator after the full cycle: merged into main (worker commit 2e527b1a, --no-ff; direct verification of the pin line and the hi-IN sync); live ja NLU suite 19/19 with the corrected row green. FIXTURE-ONLY: no deploy. The reserve number unused (no out-of-scope finding; note JF-770 was filed by the orchestrator directly as the hi-IN anchor regression).
<!-- SECTION:FINAL_SUMMARY:END -->

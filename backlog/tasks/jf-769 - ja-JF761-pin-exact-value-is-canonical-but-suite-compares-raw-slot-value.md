---
id: JF-769
title: >-
  ja JF-761 NLU pin expects canonical "Game of Thrones" but the suite's
  exact-value comparison reads the RAW slot value (row will read red on its
  next live run despite correct routing)
status: To Do
assignee: []
created_date: '2026-10-05 00:00'
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
- [ ] #1 ja pin value corrected (or comparison canonical-aware) with the reason recorded inline
- [ ] #2 Sweep: no other fixture exact-value pin has the same raw-vs-canonical mismatch
- [ ] #3 Live NLU suite run for ja-JP green on the corrected row
<!-- DOD:END -->

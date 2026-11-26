---
id: JF-975
title: >-
  nlu-lab: surface the JF-936 ERROR-row diagnostics on the human-facing surfaces
  (webapp side projection, app.js error badge/detail, run-ok summary error
  count)
status: Done
assignee: []
created_date: '2026-11-26 18:50'
updated_date: '2026-11-26 20:05'
labels:
  - nlu-lab
  - diagnostics
  - ux
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-936 simplify/altitude review round (2026-11-26, same-turn landing rule): the ERROR-row diagnostics JF-936 captures now live only inside the run JSON. Every human-facing surface still shows a bare error state with no why: (1) scripts/nlu-lab/webapp.py _side() (lines ~146-150) projects a fixed field set (selected_intent, status, slots, considered_intents) and drops row.error; (2) scripts/nlu-lab/web/app.js renders an error row as a localized "error" badge with no reason text, and the detail card (lines ~190-219) shows no error field; (3) the cli.py run-ok summary (lines ~381-383) prints "run ok: N rows, expectation failures: M" with no error-row count or text, so an all-ERROR run (the expired-auth shape JF-936 cites) reads "run ok" everywhere. Fix: project "error" in _side(), render it in app.js (detail card and/or a title on the err cell), and add an "N error rows" clause to the run-ok summary when N > 0. Small, self-contained; the run JSON schema already carries the field.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Orchestrator plan (2026-11-26): worker (own worktree, opus) surfaces the JF-936 row.error diagnostics on all three human surfaces: (1) webapp.py _side() projects "error" alongside the existing field set; (2) web/app.js renders it on the error badge (title/tooltip) and in the row detail card, following the app's existing localization/rendering style; (3) cli.py run-ok summary gains an "N error rows" clause when N > 0 (the all-ERROR case already fails loudly; this covers the partial-ERROR shape). Tests: webapp projection unit test, summary-line test (partial-ERROR run prints the clause, all-OK run does not), app.js change verified at minimum by the webapp's existing test harness if one exists (check how web/ is tested today). Verification: PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 /usr/bin/python3 -m pytest scripts/nlu-lab/tests tests/scripts -q. Dotnet DoD items N/A (python/js only).
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE, merged and pushed (worker a95d8c04 via merge + gate round 1a4c44fb). The JF-936 ERROR-row diagnostics are now on ALL FIVE human surfaces: (1) webapp _side() projects row.error; (2) app.js error badge carries the text as a native title tooltip (desktop enhancement) AND the detail card renders it as a localized line, text-only insertion throughout (status-gated so badge and detail always agree); (3) the run-ok summary appends 'error rows: N' when N > 0 plus ONE first-error stderr line (all-ERROR still fails loudly rc 1 from JF-936); (4) the compare output's error rows now carry the failure text via RowDiff.error (new-side preferred, old-side fallback); (5) the webapp run-list selector badges partial-ERROR runs with a warning count so they cannot be picked as clean diff sides. Old run files keep working (error None). Tests: node DOM-stub harness drives the real app.js for badge+detail assertions, projection and scan tests, summary/compare CLI assertions; 692/692 python tests green. Gates: /simplify 4-in-1 (finding applied: shared _NO_ERROR_TEXT fallback); /code-review high 5 findings: 4 applied, 1 skip-with-reason (tooltip-on-touch: the detail card is the phone-first universal surface, the tooltip is desktop sugar; no layout risk taken). gate-exempt on dotnet DoD items: python/js-only change.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

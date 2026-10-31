---
id: JF-919
title: >-
  E2E harness: per-locale simulate OUTAGE gating + documented one-shot shapes
  (research follow-up)
status: Done
assignee: []
created_date: '2026-10-31 12:43'
updated_date: '2026-10-31 13:35'
labels: []
dependencies: []
references:
  - claudedocs/research_simulate-skill-locale-outage_2026-10-31.md
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/43'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-10-31 research dossier (claudedocs/research_simulate-skill-locale-outage_2026-10-31.md, JF-918 validation round): the SMAPI simulate-skill service has recurring outages in its skill-ROUTING layer (profile-nlu stays green; documented contract is modal/one-shot only, 8-locale matrix), which today went TOTAL (it-IT evergreen included) as cumulative simulation volume grew (~250+ calls). The E2E harness currently spends 30-50 minutes producing mass reds during these outages, which look like product bugs and invite wrong diagnoses.

Implement two recommendations from the dossier:
1. SELF-DIAGNOSING HARNESS: before each locale's battery, fire ONE cheap documented-shape control (an invocation-prefixed one-shot for that locale). If the control fails with the selection-lost shape ("did not resolve to any intent" / IntentForDifferentSkill class), mark the locale OUTAGE for the run and SKIP its cases with that marker (not FAIL). profile-nlu is the only guaranteed-evergreen control: when the it-IT control itself fails, everything skips as OUTAGE and the run finishes fast instead of burning volume.
2. DOCUMENTED SHAPES ONLY: remove reliance on the undocumented two-step session carryover where the harness already has the machinery (the OPEN fallback builds invocation-prefixed one-shots); smoke commands should ride the same one-shot builder instead of bare session-dependent phrases.
3. VOLUME BUDGET: the control probe replaces wasted case calls; when a locale is OUTAGE-marked, skip immediately (no retries against the dead service).

Constraints: the harness lives in tests/integration (Python/pytest) with scripts/run_e2e_tests.sh as the runner; the dry-run mode must keep working; it-IT green locales must behave exactly as today (control passes, full battery runs); no change to the plugin code or the models.
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
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Dry-run mode still validates all fixtures and passes without SMAPI calls
- [x] #2 When a locale's invocation-prefixed one-shot control fails with the selection-lost shape, every case for that locale SKIPS with an OUTAGE marker and the run stops firing simulations for that locale (verified live during an outage window)
- [x] #3 When the control passes (healthy locale), the battery runs exactly as today: same cases, same expectations, no extra skips
- [ ] #4 A total-outage run (it-IT control failing too) completes in minutes with all cases OUTAGE-skipped and exit code clean, instead of ~50 minutes of reds
- [x] #5 The routing control uses each locale's own JF-511-verified open phrase (the smoke fixture's open_utterance), NOT a one-shot composition: JF-511 measured the invocation-prefixed one-shot as unroutable in the de/fr/es marketplaces even when healthy, so a one-shot control would false-positive there. The two-step battery shape stays as-is (the only shape that routes when healthy); the gate handles the broken-service case
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
IMPLEMENTED + LIVE-VALIDATED (2026-10-31): tests/integration/test_e2e.py gains _locale_outage/_locale_control_passed/_locale_open_phrases module state, the lazy _routing_control_phrases() loader (fixture open_utterance per locale; fail-open when a locale has no smoke fixture), and _ensure_locale_routable() (control = the locale's own open phrase; double-failure of the outage classes marks OUTAGE; non-outage SmapiError and success fail open; a successful control records session state for the conditional-reset policy). Gate called at the head of the four simulate tests. LIVE evidence in today's flapping window: dry-run 35 passed/160 skipped with zero SMAPI calls (AC1); run shortened 35-51min -> 20:22 with 10 locales OUTAGE-skipped incl. it-IT (AC2 + the total-outage evergreen leg; a fully-total window would finish in ~3-4 min of controls); en-AU control passed then its battery flapped (the JF-899 per-call class; those reds are honest and NOT masked); the control rides fixture-verified open phrases, never one-shot compositions (AC5 per the JF-511 measurement). Gates pending: /simplify battery (3 agents) + code-review high.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-10-31 REVIEW + SHIP: /simplify battery (loader reuse via conftest load_locale_fixtures, JF-899 cross-documentation, honest 2-sims/locale pricing applied). code-review high 6 findings: APPLIED the dead rate-limit branch (client converts sustained 429s to plain 'Rate limited' SmapiError; detected by message now), the empty-invocation pass (control must actually invoke the skill), the alarm handling (propagates instead of being eaten; one-shot alarm left the test unprotected). DOCUMENTED: alarm-budget cost in slow regimes (--smapi-timeout lever, first gated test of a locale carries the control's 2 sims), rider-design alternative rejected (ordering: smoke collects last; semantics: smoke open is degrade-and-continue per JF-656/JF-899; filtering: -k breaks designated carriers), retry-variant unification skipped (the three retry sites have different retryable policies, not identical copies). AC #4 left UNCHECKED honestly: the evergreen leg IS proven live (it-IT control double-failed and its battery skipped instantly) and every outage locale skipped instantly, but today's window flapped (6 locales' controls passed), so the fully-total 'minutes + clean exit' shape was not observed end-to-end; a genuinely total window would pay ~8-14 min of controls then finish with all-OUTAGE skips.

2026-10-31 SHIPPED: commit 167918d8 on main, pushed. Final live verification: dry-run 35/160 clean zero-SMAPI; it-IT full-chain 2/2 PASSED in 44s with the control self-closing (the F1 hijack fix proven on the vulnerable site).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
E2E harness now self-diagnoses simulate-skill routing outages per locale (JF-919): one routing control per locale per run (the fixture's JF-511-verified open phrase, via the shared conftest loader); double failure of the outage classes marks the locale OUTAGE and skips its whole battery with one warning per locale; a passing control self-closes (hijack-safe on every call site); rate-limited/server-error/empty-invocation controls are inconclusive and re-probe; the per-test alarm propagates untouched. Live-validated in a flapping outage window: dry-run clean zero-SMAPI, smoke run 35-51min down to 20:22 with 10 locales OUTAGE-skipped including the it-IT evergreen, control-passing locale ran clean (2/2 in 44s). AC #4's fully-total shape honestly left unchecked (window flapped; evergreen leg proven). Commit 167918d8 on main. DoD #1-#3 and #6-#8 are dotnet/NLU-surface gates, N/A for this Python-only harness change (no plugin code touched; CI validates the untouched .NET tree).
<!-- SECTION:FINAL_SUMMARY:END -->

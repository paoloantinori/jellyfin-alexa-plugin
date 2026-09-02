---
id: JF-400
title: >-
  11 of 17 locales have zero NLU/E2E test coverage - extend fixtures starting
  from pt-BR, ja, hi, en-variants
status: In Progress
assignee: []
created_date: '2026-08-23 05:57'
updated_date: '2026-09-02 16:49'
labels:
  - testing
  - nlu
  - localization
milestone: m-16
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Maturity finding (2026-08-23 assessment). NLU fixtures exist for only 6 of 17 locales (it-IT 125, en-US 118, de-DE 61, fr-FR 59, en-GB 57, es-ES 54 utterances); E2E effectively covers it-IT only (en-US E2E documented unreliable, competes with built-in skills). 11 locales (including all es-MX/es-US/fr-CA/en-IN/en-CA/en-AU/pt-BR/nl-NL/ar-SA/hi-IN/ja-JP) have zero test coverage: their model quality is unverifiable and regressions from sample changes land silently.

Plan: extend NLU fixtures locale by locale. Not all 11 are equal priority: pt-BR, ja-JP, hi-IN, en-AU/CA/IN (variant English, cheap to derive from en-GB/en-US) first. Each fixture needs the locale's real sample vocabulary cross-referenced against its slot types (anti-pattern #8). This pairs with JF-399 (sample parity): parity work without fixtures is unverifiable.
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
2026-08-23 partial (commit f146dfb): en-AU/en-CA/en-IN fixtures derived from en-GB (57 utterances each), live-verified on profile-nlu. Fixed a stale en-GB case inherited by derivation ('Repeat the song' -> replaced with the real sample 'Repeat this song forever'). Remaining uncovered: pt-BR, ja-JP, hi-IN, ar-SA, nl-NL, es-US, es-MX, fr-CA (need language work).

Caveat discovered while running suites: profile-nlu is NONDETERMINISTIC at the infrastructure level - individual cases fail in full-suite runs (sometimes Amazon 500s) but pass when re-run in isolation. Do not treat a single full-suite failure as a regression without re-running the case alone. Candidate improvement (separate task if wanted): add a retry-on-5xx to SmapiClient.profile_nlu.

2026-08-23 second batch (commit 66b340b): es-MX + fr-CA fixtures added and fully green (116/116 live). Divergences annotated per-case (fr-CA album-vs-song greedy capture; es-MX muestrame fallback). es-US fixture REVERTED after ~20 systematic divergences -> new task JF-406. Coverage now 11/17 locales (it, en-US/GB/AU/CA/IN, de, fr-FR/CA, es-ES/MX). Remaining 6 (pt-BR, ja, hi, ar, nl, es-US): pt/ja/hi/ar/nl are NOT active locales on the dev skill so cannot be tested against profile-nlu at all; es-US blocked by JF-406. This task can close when those blockers resolve (enable locales / fix divergence), or be kept open as tracking.

PROGRESS 2026-08-29 (device-free, JF-414 spillover): es-US.yaml and pt-BR.yaml fixture files CREATED (first fixtures ever for both locales), each probe-verified against the pushed model BEFORE asserting (the es-US lesson: its imperative 'Reproduce un album de queen' diverges to PlaySongIntent - recorded in JF-406 - so only the working bare form is asserted; pt-BR is clean, all three album forms + browse green 4/4 live). Remaining uncovered NLU locales: ja-JP, hi-IN, ar-SA, nl-NL (no fixture files; nl/hi/ar models are live on the vendor, ja-JP is vendor-disabled 404).

COMPLETION 2026-08-29 (device-free): fixture files created for the remaining live locales - es-US, pt-BR, nl-NL, hi-IN, ar-SA - each probe-verified against the pushed models BEFORE asserting (12/12 green live). NLU coverage now 16/17 locales; only ja-JP remains (vendor-disabled, SMAPI 404, manifest enablement needed - tracked in JF-414). Harness fixed: INVOCATION_PREFIX gained ar-SA/nl-NL/hi-IN (the English fallback misroutes Arabic). Two JF-406-class divergences found and documented in-file: es-US imperative 'Reproduce un album de queen' -> PlaySongIntent; ar-SA 'shughl album queen' bare -> PlayVideoIntent deterministically (4/4), correct with the Arabic invocation prefix. The 2026-08-23 note that pt/ja/hi/ar/nl were 'not active on the dev skill' is superseded: all but ja-JP are live and testable (verified by direct model reads and green runs).

PER-USER NOTE for fixture extension: always probe-verify each utterance bare on profile-nlu BEFORE writing the expectation; the sample vocabulary alone does not predict routing (both divergences above are in forms present as samples).

JF-450/451 follow-up (2026-09-02): SetReminderIntent is now declared in all 17 models; NLU fixtures gained routing entries for 11 locales + an e2e_it-IT routing entry, but es-US/pt-BR/nl-NL/hi-IN/ar-SA have no SetReminder fixture entries yet (their seeds lack SleepTimer entries too). Extend under this program using the existing probe-verify-first rule.
<!-- SECTION:NOTES:END -->

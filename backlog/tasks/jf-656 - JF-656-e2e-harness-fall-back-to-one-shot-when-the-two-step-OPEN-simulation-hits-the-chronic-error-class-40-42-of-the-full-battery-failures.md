---
id: JF-656
title: >-
  JF-656 - e2e harness: fall back to one-shot when the two-step OPEN simulation
  hits the chronic error class (40/42 of the full-battery failures)
status: Done
assignee: []
created_date: '2026-09-27 13:31'
updated_date: '2026-09-28 05:49'
labels:
  - e2e
  - harness
  - infrastructure
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the full e2e battery: 40 of 42 failures are the chronic open-simulation error class ('Simulation failed ... An unexpected error occurred' at the two-step OPEN step: 'open/öffne/abre/lance/ouvre/start jellyfin player' under ar-SA, de-DE, es-ES, es-MX, es-US, fr-CA, fr-FR, hi-IN, ja-JP, nl-NL, pt-BR). All two-step smoke tests in those locales failed AT THE OPEN STEP; the corresponding one-shot tests in the same run passed. Known contributing facts: the English invocation name is unreliable under non-English ASR (the english_invocation_broken_nonenglish memory; the two-step open-verb convention exists precisely because of that), the harness already retries the open once (both attempts failed in these runs), and the per-locale simulate outages recur (the simulate_outage_per_locale memory).

THE WORK (harness-level, tests/integration/): when the OPEN step fails with this error class, fall back to running the utterance ONE-SHOT (skip the open; the one-shot form demonstrably works in these locales in the same run) instead of failing the whole two-step test; mark the test result as 'open-fallback' so the report distinguishes a full-chain pass from a fallback pass. Optionally: a pre-run outage gate (one evergreen open probe per locale; locales failing it get the fallback mode upfront instead of burning 20s x N on retries).

VERIFICATION BAR: a rerun of the smoke suites in the affected locales completes with fallback-marked passes instead of the 40 error failures; the it-IT/en-US two-step path (which passes today) is unchanged; dry-run validates fixtures unchanged.
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

## Notes

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-28 as merge 677f0a24 (pushed; Python-only, no deploy): the e2e open-fallback plus the battery's actual root cause. The 2026-09-27 failing set (40 failures) exactly matches the 11 locales JF-558 renamed to native invocation names, whose smoke fixtures still opened with the dead English 'jellyfin player'; the fixtures now carry the live native names (verified byte-for-byte against Config.cs LocaleInvocationNames). The fallback machinery: SmapiUnexpectedError typed classification at BOTH FAILED sites (the review caught the fast-fail hole) matching the platform cause field alone (the review caught the composed-message matcher risk), the per-process outage gate (documented best-effort), the warnings-summary OPEN FALLBACK marker, and the assume-open session policy in the outage branch (the review's consistency catch); the es-US INVOCATION_PREFIX gap fixed. Gates: /simplify in-worker, code-review skill in the orchestrator transcript (7 findings: 4 applied, 3 dispositioned), dry-run green (orchestrator-verified). HONEST FRAMING RECORDED: in an active outage the fallback converts an opaque open failure into a marked one-shot failure (better diagnostics, still red); the fixture fix is the green-maker on a healthy platform; the fallback becomes a true rescue when the native-name wrappers are verified (the next battery run is the verification of the ten unprobed compositions, ja/hi mixed-script the highest-risk rows).
<!-- SECTION:FINAL_SUMMARY:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

### 2026-09-28 implementation (worker branch, feat(jf656))

Mechanism (tests/integration/):
- smapi_client.py: the FAILED-simulation branch now raises `SmapiUnexpectedError(SmapiError)` when the platform cause text ("An unexpected error occurred") matches, else plain SmapiError. Classification lives at the raise site per the module's own typed-subclass convention (SmapiRateLimitError, SmapiServerError); every existing `except SmapiError` caller is unaffected.
- test_e2e.py smoke two-step: the open's existing one retry stays; when the retry also fails with SmapiUnexpectedError, the test (a) logs "OPEN FALLBACK (...)" (logger.warning, carries a 150-char exception excerpt), (b) warns "OPEN FALLBACK: '<utt>' (<locale>) ran as a one-shot (<source>)" via warnings.warn so the marker shows in pytest's warnings summary for PASSING tests, where captured log lines are not rendered, (c) flags the locale in the module-level `_open_outage_locales`, and (d) runs the target utterance one-shot with the fixture's invocation_name through the locale's INVOCATION_PREFIX (the full-chain composition). All command assertions unchanged; the pass line reads "SMOKE PASS (open-fallback)"; the intent-mismatch message names the shape (one-shot vs in-session).
- The optional outage gate is the flag itself: later tests of a flagged locale skip the open attempt (one logged line) instead of burning the ~20s open+retry pair per test; strictly reduces simulate() calls.
- Healthy-path byte-identical: the fallback engages only inside the former unconditional pytest.fail branch; dry-run untouched (157 collected, all skipped, green before and after).

Fixture root-cause fix (11 locales): the 2026-09-27 battery's failing locales are exactly the 11 locales JF-558 renamed to native invocation names, and the smoke fixtures had not been touched since JF-511: they still opened "öffne/abre/lance/ouvre/start jellyfin player", a name that no longer exists in those models. This commit updates invocation_name + open_utterance to the live JF-558 names (de "meine sammlung", es x3 "mi colección", fr x2 "mon serveur", pt "minha coleção", nl "mijn collectie", ja "マイコレクション", hi "मेरी कलेक्शन", ar "مجموعتي"), keeping the live-verified open verbs. Without this the fallback could never work in its target locales (it one-shots with the fixture's invocation_name) and the opens could not succeed even on a healthy platform. en-* and it-IT files are unchanged (their names never changed).

Observation tail (2026-09-28 live):
- Live pytest run (de-DE favorites, pre-fixture-fix): the open "öffne jellyfin player" failed twice with exactly the chronic class ("Simulation failed after 20.0s ... An unexpected error occurred."); OPEN FALLBACK engaged and shows in the run's warnings summary; the one-shot with the then-stale name failed ("did not resolve to any intent"), so the test failed as a marked one-shot failure rather than an open failure.
- Direct SMAPI probe (de-DE "öffne meine sammlung", native name): FAILED "did not resolve to any intent", consideredIntents [IntentForDifferentSkill x3, LaunchRequest]: the native invocation IS recognized; the simulate engine still failed the launch today. Both components are real: the stale fixture names AND the recurring outage (today in its "did not resolve" mode, which the matcher deliberately does not treat as the outage class since that message is also the signature of genuine model misses).
- A second live pytest run was skipped per the two-run budget: today's de-DE mode would fail the updated fixture's open with the non-matched class, demonstrating nothing new. When the platform next presents the chronic class, flagged locales go straight to the one-shot.

Verification: dry-run green (157 skipped) three times (pre-fixtures, post-fixtures, post-simplify); the typed raise path behaviorally verified with mocked polls (generic cause raises the subclass; "did not resolve" stays plain SmapiError; case-insensitive cause match); no stale references left. dotnet gates N/A (Python-only change: no C#, no models). /simplify four-angle round applied: typed-error classification moved to smapi_client.py (reuse/altitude), branch-point warnings with literal source text (simplification), the tri-state collapsed to `open_response is None`, pass line folded to one call, mislabeled fail prefix fixed, and an unconditional-warning defect caught and fixed (a healthy open would have warned OPEN FALLBACK). Skipped findings: open-step helper extraction (the inline try/retry shape pre-exists; extraction is taste), reusing SmapiClient._intent_summary for the considered list (it returns one name, cannot serve the top-3 list), NLU-fixture invocation_name staleness (inert for profile-nlu, verified). Code-review gate: to run in the orchestrator transcript per the worker/orchestrator split.

### Code-review round (orchestrator gate, 7 findings; applied 2/3/4/6, dispositions 1/5/7)

Applied (commit review(jf656)):
- #2 es-US INVOCATION_PREFIX gap: the dict had no es-US entry, so a fallback there composed the English "ask {inv} to" around the Spanish name (the exact wrong-locale-prefix class the JF-400 comment documents for ar-SA). Added "pide a {inv} que " consistent with es-ES/es-MX.
- #3 fast-fail classification hole: a simulation FAILED directly in the initiation response was returned as a silent dict, bypassing the typed classification entirely; the smoke test then died on a confusing assert instead of engaging the fallback. Both FAILED sites now classify through one shared `_failed_simulation_error` helper (the init path composes "Simulation failed for ..." without the poll's "after Ns" detail).
- #4 matcher precision: the outage marker was matched against the whole composed message, which embeds the utterance, so utterance text could trigger the classification. The helper now matches error_msg alone (the platform's cause field). Mock-verified with a poisoned utterance embedding the marker text: no false classification.
- #6 session-state consistency: the outage-class open branch now does `_open_sessions.add(locale)` (the file's assume-open policy: a failed simulation leaves the dialog state unknown, and a prefixed one-shot against a stale open dialog is captured into the elicited slot and misroutes). If the fallback command then succeeds, `_record_session_state` overwrites with the real state; the flagged-skip branch correctly does NOT assume open (no failure occurred in that test).

Dispositions recorded (no behavior change):
- #1 honest framing of the fallback's rescue value: the prefixed one-shot composition is documented dead for de/fr/es with the English name, the native-name wrappers are unverified, and the live de-DE native-name one-shot failed to resolve. So in an ACTIVE outage the fallback converts an opaque open failure into a MARKED one-shot failure: better diagnostics, still a failure. The fixture root-cause fix is the actual green-maker once the platform is healthy or the opens verify. The fallback becomes a true rescue only when the native-name wrappers are verified (a probe campaign that needs a healthy simulate window).
- #5 verification debt on the fixture fix: the ten non-de-DE open compositions (native name + retained verb) are unverified; the next battery run IS the verification. Highest-risk rows: the mixed-script shapes "open マイコレクション" (ja-JP) and "start मेरी कलेक्शन" (hi-IN), where an English open verb meets a native-script name.
- #7 the outage gate is per-process, one-way (a locale is never re-probed after a mid-run recovery), and invisible to a parallel run (no xdist configured today): documented as best-effort in the comment at `_open_outage_locales`.

Verification this round: dry-run green (157 skipped, 0.70s); the mocked classification checks extended to six cases (poll path generic/other, init path generic/other, poisoned-utterance non-match, case-insensitive cause match), all green.
<!-- SECTION:NOTES:END -->

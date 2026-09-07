---
id: JF-511
title: >-
  E2E suite for the other 15 locales: design study (value vs cost vs
  simulate-skill reliability per locale; full mirror vs smoke subset vs family
  sampling)
status: To Do
assignee: []
created_date: '2026-09-06 19:13'
updated_date: '2026-09-07 00:25'
labels:
  - e2e
  - i18n
  - design-study
dependencies: []
references:
  - JF-510
  - run_e2e_tests.sh
  - the en-US flakiness lesson in CLAUDE.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-06 direction: today only it-IT (and a small flaky en-US set) have e2e fixtures (simulate-skill based, asserting full responses against the live skill+server); the other 15 locales have NLU fixtures only (profile-nlu, intent/slot routing in isolation). This task is a DESIGN STUDY first, not an implementation order: decide whether and how to build an equivalent full-pipeline suite for the other locales.

QUESTIONS TO ANSWER (study, with live evidence):
1. VALUE: what does an e2e pass catch that the NLU suite does not, per locale? (handler behavior is locale-independent in large part; the locale-specific surface is routing + fill + response strings. If routing is already covered by profile-nlu NLU fixtures, e2e's marginal value per locale may be mostly the response-string layer and cross-layer fill interactions like JF-492/JF-509 strip healing per language family.)
2. COST and RISK: simulate-skill wall-clock x 15 locales (SMAPI delay 1.5s per call; today's 58-test it-IT suite takes minutes; x16 scales accordingly), SMAPI rate limits, and the en-US lesson (competition with built-in skills makes simulate-skill flaky - measure whether de-DE/fr-FR/es-ES simulate-skill is as reliable as it-IT or as flaky as en-US before committing).
3. SHAPE OPTIONS: (a) full mirror of the it-IT suite per locale (highest cost); (b) a SMOKE subset per locale (5-8 tests: one play per media type, one disambiguation, one strip-family case per language family - the JF-509 noun tables are per-language and only e2e can prove the strip heals real fills); (c) locale-family sampling: full e2e for one representative per family (de/fr/es as family heads) + NLU-only for the rest. Recommend one with evidence.
4. PREREQUISITE: the JF-510 it-IT refresh lands first, so the new suite generation starts from a known-good fixture pattern (specific asserts, no response_type:any).
Deliverable: a decision document in the task notes (value/cost/risk per option, recommendation, measured simulate-skill reliability for at least de-DE/fr-FR/es-ES) - then, if the answer is yes, a scoped implementation task with the chosen shape.
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
## Decision document (measurement phase, 2026-09-07, live evidence)

Environment: skill amzn1.ask.skill.33dfacd5-3676-4cdc-8b02-81efb227df83 (re-discovered via list-skills-for-vendor, matches env), development stage, manifest build SUCCEEDED. 56 simulate-skill calls plus profile-nlu / get-interaction-model / get-skill-status probes, SMAPI_DELAY=1.5, zero 429/throttle events. Raw per-call JSON log kept at /tmp/jf511_results.jsonl (scratch). No test or fixture files were modified.

### 1. simulate-skill reliability per locale (measured)

| Locale | One-shot prefix shape (frage/ouvre/pide/ask {inv} ...) | Two-step shape (open verb, then bare in-session command) | Root cause of failure |
|---|---|---|---|
| it-IT (baseline) | 3/3 OK, avg 6.8s | n/a (not needed) | - |
| en-US (baseline) | 2/2 OK (7.0s, 12.4s) | n/a | - |
| es-MX | 1/1 OK (16.3s) | not probed (one-shot works) | - |
| de-DE | 0/10 (four alternative grammatical forms + one bare, all no-intent) | opens 4/4 (avg 11.7s), in-session commands 3/3 (avg 6.8s) | one-shot invocation grammar never opens the skill; model saved+built, content matches repo |
| fr-FR | 0/6 (incl. infinitive form) | opens 2/2, command 1/1 | same |
| es-ES | 0/6 | opens 2/2, command 1/1 | same |
| fr-CA | 0/2 | not probed | same |
| en-GB | 0/2 (platform 'An unexpected error occurred', ~21s) | open 1/1 (skill invoked despite IntentForDifferentSkill first in consideredIntents, the documented competition pattern) | platform-side; transient vs persistent unproven |
| nl-NL, pt-BR, ar-SA, hi-IN, ja-JP | impossible: 'No interaction model was found for the specified locale' | impossible | MANIFEST GAP: Alexa/Manifest/manifest.json declares only 12 locales; simulate-skill requires the locale in the manifest. profile-nlu still works for all of them (saved models exist for 17/17) |

Supporting facts: (a) get-skill-status shows all 12 manifest locales SUCCEEDED (quick+full+dialog+NFI builds), ruling out saved-but-unbuilt for de/fr/es; (b) saved es-ES/fr-FR models match the repo JSON (samples present, intent counts equal), ruling out stale deploy; (c) es-ES 0/6 vs es-MX 1/1 with identical saved-model content means the one-shot prefix failure is Amazon per-marketplace invocation-grammar behavior, not our model; (d) the JF-510 no-invocation artifact (SUCCESSFUL sim with empty skillExecutionInfo) did NOT occur in the 21 successful sims here; all failures were simulation-level FAILED statuses; (e) failures cost nearly as much wall-clock as successes (7-21s), so an unreliable locale burns the full time budget.

### 2. Wall-clock cost model

Per simulate() call (submission ~2s, then 3s-interval polls, incl. 1.5s pacing): successful in-session command ~6.8s; open (Launch + resume APL) ~11.7s avg; prefixed one-shot ~6.8s (it-IT) / 12-16s (en-US random, es-MX); failure ~7s (no-intent) to ~21s (en-GB platform error).

Extrapolations:
- Smoke 8 tests x 12 manifest locales, two-step shape: 96 x (11.7 + 6.8 = ~18.5s) = ~30-40 min per full run.
- Smoke 5 tests x 12 locales: ~20 min.
- All 15 locales requires the manifest fix first; then 8 x 15 = 120 x 18.5s = ~40-50 min.
- Full it-IT mirror (58 tests) x 15 locales two-step: 870 x 18.5s = ~4.5 h (one-shot shape where it works ~1.9 h). Impractical as a routine gate.
- Today's it-IT 58-test suite at ~7s/test = ~7 min plus autouse reset sims, consistent with the observed 'minutes'.

### 3. Value evidence (de-DE, full pipeline via the JF-510 response shape)

- Play: bare in-session 'spiele meine favoriten' -> PlayFavoritesIntent, skill invoked, response body (result.skillExecutionInfo.invocations[*].invocationResponse.body.response) carries AudioPlayer.Play, shouldEndSession=true. Same outcome for fr-FR ('lis mes favoris') and es-ES ('reproduce mis favoritos').
- Not-found: 'spiele lieder von xyzzyfoo' -> German speech 'Ich konnte keinen Benutzer namens xyzzyfoo finden.' (UserByNameNotFound), session ended correctly. Bonus finding: de-DE NLU routes this artist-shaped utterance into PlayFavoritesIntent(username), not PlayArtistSongsIntent; NLU-visible but caught here first.
- Locale string/SSML layer (the e2e-only value): opens in de/fr/es/en-GB returned localized SSML resume prompts (emphasis, break tags) plus APL RenderDocument and Dialog.UpdateDynamicEntities; all well-formed.
- Simulator quirk: en-US 'play something random' (no media_type) returned a 'content requires a screen' response with NO AudioPlayer.Play (random selection picked video content on the screenless simulator device): random-media fixtures must pin media_type.
- Counter-evidence on family sampling: es-ES 0/6 where es-MX 1/1 (identical model content), en-GB platform-error where en-US 2/2. A family head does not predict its family at the invocation layer.

### 4. Recommendation

(b) Smoke subset (5-8 tests/locale). NOT full mirror, NOT family sampling. Rationale: handler logic is locale-independent and already covered by the it-IT suite plus ~2800 unit tests; the per-locale marginal value is response strings, SSML, and cross-layer slot-fill, which a 5-8 test smoke covers (one play per media type, one disambiguation, one strip-family case, one not-found). Full mirror costs ~4.5 h for what the smoke proves in ~40 min. Family sampling is unsound because invocation-layer behavior diverges INSIDE families (measured, see 3).

Prerequisites, in order:
1. Manifest fix: add ja-JP, pt-BR, ar-SA, nl-NL, hi-IN to Alexa/Manifest/manifest.json publishingInformation.locales (with localized name/summary/description blocks) and redeploy; verify via get-skill-status that 17 locales build. Without this, 5 of 15 locales cannot simulate at all. This is a separate repo task and also the reason ja-JP/ar-SA/hi-IN cannot even be probed today.
2. Two-step harness mode: for non-it locales, open with the locale's invocation verb (oeffne/ouvre/abre/open ...) then send the bare command in-session; keep the one-shot prefix only for locales where it is proven (it-IT, en-US, es-MX) via a per-locale capability probe. The current INVOCATION_PREFIX one-shot forms for de/fr/es are measured-dead (0/24 across grammatical forms).
3. Locale-scoped session reset (the harness comment's known limitation): the autouse reset hardcodes it-IT; extend it to reset in each fixture's own locale, and make resets conditional (only when the previous response left shouldEndSession != true) so the smoke suite does not pay a reset per test.
4. ja-JP NLU fixture first: ja-JP has NO NLU fixture today (16 locale NLU fixtures exist); NLU coverage is a prerequisite for e2e there.
5. Fixture authoring notes: pin media_type on random-play tests (simulator device is screenless); artist not-found probes should avoid hoere {musician}-shape carriers (observed NLU no-fill on garbage Musician tokens; mechanism unverified).

Sequencing: JF-510 refresh -> manifest fix (prereq 1, own task) -> two-step harness + locale reset (prereqs 2-3) -> generate 5-8 test smokes per locale from the models' own samples. Status stays To Do; the orchestrator decides execution.
<!-- SECTION:NOTES:END -->

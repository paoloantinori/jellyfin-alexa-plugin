# Research Report: SMAPI simulate-skill locale failures vs profile-nlu (issue #43 follow-up)

**Date**: 2026-10-31
**Depth**: standard (+ live empirical probes on our own skill)
**Confidence**: HIGH on layer discrimination and documented constraints; LOW on the trigger of today's total outage (Amazon-side, not observable from outside)

## Executive Summary

The mass E2E failures ("This utterance did not resolve to any intent in your skill") are in the skill-ROUTING layer of Amazon's simulation service, not in our models, catalog wiring, endpoint, or deploy: profile-nlu resolves the same utterances to the right intents, the live fr-FR model keeps its catalog wiring, and the failures reproduce even for the documented one-shot invocation shapes, now including the it-IT evergreen. The Simulation API's documented contract only supports modal and one-shot invocations ("{ask|open|start} <invocation> ..."; no multi-turn), and its documented locale matrix is narrower than our 17 locales. The actionable fix is on our side: make the E2E harness self-diagnosing (a cheap per-locale control probe that marks OUTAGE locales instead of failing), use documented one-shot shapes only, and budget the simulation volume; keep profile-nlu as the model-side truth, which is immune to this outage class.

## Findings

### 1. What Amazon documents about the simulation service

- The Skill Simulation API request carries `device.locale`; the documented valid values are only eight: en-US, en-GB, en-CA, en-AU, de-DE, fr-FR, en-IN, ja-JP. Several locales our suite simulates (it-IT, es-ES, es-MX, es-US, fr-CA, hi-IN, nl-NL, pt-BR, ar-SA) are outside that documented matrix. The list is stale (it-IT works normally for us), but it documents that simulation support is deliberately a subset with its own per-locale health. [1]
- The official test-automation lab states the contract explicitly: "Simulation API currently does not support multi-turn dialogs. That being said you can only test modal invocations (e.g. 'ask number guessing') or one-shot invocations (e.g. 'ask number guessing for help')" and the input "needs to be a valid modal or one-shot invocation starting with '{ ask | open | start } <invocation> ...'". Bare follow-up utterances that rely on a carried-over session (our two-step harness shape) are outside the documented contract. [3]
- `session.mode` (`FORCE_NEW_SESSION` / `DEFAULT`) influences the simulated dialog's session behavior; the API also defines 429 rate-limit responses with exponential backoff (no numeric quota is published). [1]
- The same "did not resolve" error with `IntentForDifferentSkill` appears in community reports going back years, including for the en-US primary locale with correct models, i.e. selection-layer failures are a long-standing, recurring simulation-service behavior, not new. [2]

### 2. What today's live probes discriminated (our skill, 2026-10-31)

- Layer discrimination (HIGH, mechanical): profile-nlu on the same live development model resolves the failing utterances to the correct intents on fr-FR ("lis les chansons de pink floyd" → PlayArtistSongsIntent, "joue un morceau aléatoire" → PlayRandomIntent) and en-US ("play songs by pink floyd" → PlayArtistSongsIntent). The failure therefore sits in invocation routing/skill selection, which profile-nlu does not exercise. Our endpoint is not even called when selection fails.
- Catalog wiring intact (HIGH): the live fr-FR model still carries `valueSupplier.valueCatalog` on JellyfinArtist and AlbumName, refuting the "restart redeployed static models and stripped the catalog" hypothesis.
- Enablement ruled out for the current total outage (HIGH, mechanical): after `ask smapi set-skill-enablement` succeeded, the it-IT one-shot control still failed selection.
- Timeline (HIGH, observed): E2E run 1 (52 min, ~180 simulations) it-IT fully green, 72 non-it failures; control run (36 min, ~78 simulations) 69 failures; ~2 h later even the it-IT evergreen one-shot ("chiedi a mia collezione di suonare i pink floyd") fails while profile-nlu stays green. The degradation broadened as cumulative simulation volume grew, then became total.
- Trigger (LOW, unverifiable from outside): consistent with a vendor-account-level degradation of the simulation routing service, plausibly induced or worsened by today's ~250+ simulations (the 429/backoff machinery exists, though the surfaced error is a selection failure rather than a 429). No Amazon status page or forum thread confirms a platform incident today.

### 3. What this means for the JF-918 validation verdict

The fix's no-regression verdict stands: the failing layer is upstream of our endpoint; it-IT was fully green through the new funnel during the same window in which non-it locales were red; the live simulator battery and logs were clean.

## Recommendations (the solution)

1. **Self-diagnosing harness (primary)**: before the battery, fire one cheap, documented-shape control per locale (invocation-prefixed one-shot). A locale whose control fails is marked OUTAGE and its cases are skipped with that marker, not FAILED. Green then means "code healthy", OUTAGE means "simulation service unavailable for that locale", and the suite stops burning volume against a dead service. This also encodes the existing evergreen-control rule into the tool instead of relying on operator memory.
2. **Documented shapes only**: stop relying on the two-step open + bare-command carryover anywhere in the E2E harness (outside the documented contract); use invocation-prefixed one-shots, for which the models already carry wrapper families (the JF-548/JF-551 wrapper rows).
3. **Budget the volume**: cap simulations per run (the 17-locale sweep is ~250 calls at 1.5 s delay); raise SMAPI_DELAY for full sweeps or split locales across scheduled runs; skip OUTAGE locales immediately rather than retrying.
4. **Model truth stays with profile-nlu**: the NLU suite is immune to this outage class and already gates model changes in CI; treat simulate-skill E2E as endpoint-integration coverage, gated by recommendation 1.
5. **Outage runbook**: when the evergreen control itself fails, re-run `set-skill-enablement` once (cheap, harmless, rules out enablement), wait, re-probe once; do not diagnose models, catalog, or endpoint while the evergreen is down (that ordering cost time today until the probes above set the layer straight).

## Confidence Assessment

- HIGH: failing layer is skill-selection routing, not model/catalog/endpoint/deploy (profile-nlu green, wiring intact, enablement ruled out, endpoint untouched on failures).
- HIGH: documented constraints (locale matrix, multi-turn unsupported, one-shot-only contract) per Amazon's own docs and lab.
- LOW: the precise trigger of today's total outage (Amazon-side; volume-induced throttle is plausible but unproven; no public incident found).
- Not verifiable from outside: per-vendor simulation quotas and Amazon-side health dashboards.

## Sources

1. Skill Simulation API reference (locale matrix, session.mode, 429 semantics): https://developer.amazon.com/en-US/docs/alexa/smapi/skill-simulation-api.html
2. Stack Overflow: simulate-skill returning IntentForDifferentSkill / "did not resolve" with healthy models (2019, en-US): https://stackoverflow.com/questions/59058251/
3. Amazon official test-automation lab 03 (modal/one-shot only; input must start with ask/open/start + invocation): https://github.com/alexa-samples/skill-sample-nodejs-test-automation/blob/master/labs/lab03.md
4. Our own live probes this session (profile-nlu vs simulate-skill on the same utterances; enablement re-set test; timeline across two E2E runs), recorded in the JF-918 backlog notes and /tmp/e2e_jf918.log, /tmp/e2e_it_control.log.

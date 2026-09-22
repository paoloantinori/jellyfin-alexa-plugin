# Research Report: SMAPI simulate-skill per-locale failures (es/fr/de/ja fail while it-IT/en-US pass)

**Date**: 2026-09-22
**Depth**: exhaustive
**Confidence**: HIGH (for the failure characterization), MEDIUM (for the root-cause attribution)

## Executive Summary

The per-locale simulate-skill failures are NOT our bug, NOT a model-build issue, and NOT a session-carryover artifact: a new FORCE_NEW_SESSION cross-check shows es-ES failing deterministically with `consideredIntents: [<IntentForDifferentSkill> x4]` for BOTH our native and the old English invocation name, while the same probe for it-IT invokes correctly and the es-ES interaction model builds SUCCEEDED throughout. The failure sits in the simulator's per-locale invocation-matching layer (Amazon-side), and Amazon's own documentation confirms the simulator's locale support is a historical subset (8 locales documented, stale, not matching reality). No public SMAPI status page exists; community evidence documents both session-reuse and non-diagnostic-error-text traps that this report's probes now exclude for our case.

## Findings

### 1. The failure is deterministic per locale, name-independent, session-mode-independent (our probes, today)

New evidence gathered this hour (all with `--session-mode FORCE_NEW_SESSION`, serialized single probes):

| Probe | Result |
|---|---|
| es-ES, native name («pide a mi colección que reproduce mis favoritos») | NOT INVOKED, `consideredIntents: [<IntentForDifferentSkill> x4]` |
| es-ES, OLD English name («pide a jellyfin player que reproduce mis favoritos») | NOT INVOKED, `consideredIntents: [<IntentForDifferentSkill> x4]` |
| it-IT control, same shape («chiedi a mia collezione di riprodurre i miei preferiti») | INVOKED, `PlayFavoritesIntent` |
| es-ES model build status | SUCCEEDED (no es rebuild since the 17-locale rebuild 2 days ago) |

`<IntentForDifferentSkill>` in `consideredIntents` is the routing layer saying "this utterance belongs to OTHER skills": the invocation-name match itself is failing for the es virtual device, for ANY name. Combined with the 2026-09-21 old-model cross-check (a 09-19 en-US model deployed: bare-open still failed, one-shot passed; content-independent), the failure is not our model's content either.

### 2. The simulator's locale support is a documented subset that lags reality (HIGH confidence)

Amazon's Skill Simulation API reference lists `device.locale` valid values as only **8 locales**: `en-US, en-GB, en-CA, en-AU, de-DE, fr-FR, en-IN, ja-JP` (developer.amazon.com/en-US/docs/alexa/smapi/skill-simulation-api.html, retrieved 2026-09-22). Neither it-IT (our WORKING locale) nor es-ES/fr-CA/pt-BR/nl-NL/ar-SA/hi-IN are documented. The doc list is provably stale (it-IT works), which means: the simulator's locale capacity has always been unevenly documented and has silently drifted; per-locale routing state is Amazon-managed and inconsistent. This is infrastructure-level evidence supporting the "their side" attribution, though it does not explain why three DOCUMENTED locales (de/fr/ja) fail.

### 3. Session carryover is a real documented trap, but excluded for our case (HIGH confidence)

The API exposes `session.mode: FORCE_NEW_SESSION` precisely because the DEFAULT mode can carry dialog state between simulations, and the community-documented `IntentForDifferentSkill` failure was resolved by `--force-new-session` in the original 2019 report (stackoverflow.com/questions/59058251, answered 2019-12-10). Our FORCE_NEW_SESSION probes still fail for es-ES; session carryover is excluded as OUR cause.

### 4. The error text is non-diagnostic (MEDIUM confidence, community-sourced)

A 2020-03 report shows `ask simulate` returning the same "This utterance did not resolve to any intent" with `IntentForDifferentSkill` even after `--force-new-session`, where the root cause was skill-side deploy mismatch (stackoverflow.com/questions/60872367). Implication: the error string alone cannot attribute blame; our attribution rests on the A/B matrix (both names fail, it/en pass, builds green, profile-nlu green), not on the error text.

### 5. Concurrent-request constraint documented, excluded by serialization (HIGH confidence)

The Skill Simulation API v1 reference states "Concurrent requests per user is currently not supported" with HTTP 409 semantics (developer.amazon.com/it-IT/docs/alexa/smapi/skill-simulation-api-v1.html). Our failing probes today were serialized single requests (one in flight, 6s poll), excluding concurrency as the cause of the es determinism. (Historical note: yesterday's battery ran probes with 2-3s delays, so 409-class interference cannot be excluded for THAT run, but today's serialized failures reproduce without it.)

### 6. Upstream-layer precedents exist (MEDIUM confidence, community-sourced)

- A 2025-08 report (stackoverflow.com/questions/79992022): a dev-stage skill failed EVERY invocation phrase in the console simulator AND on a real device, while direct Lambda invocation worked; CloudWatch confirmed the request never reached the skill: "failure is entirely upstream, at Alexa's invocation/device-support layer."
- A 2018 ask-sdk issue (github.com/alexa/alexa-skills-kit-sdk-for-nodejs/issues/420, opened 2018-06-25): "The es-ES locale seems to be slightly broken": the es-ES simulator delivered intent requests missing the slot value while en-GB behaved. Historical, closed, but evidence the simulator has had locale-specific defects before.

### 7. No public SMAPI status page (HIGH confidence)

No Amazon-published health dashboard for SMAPI or the simulator exists; outage confirmation from the vendor is structurally unavailable. NOT_FOUND for any 2026 outage notice.

## The de-anchored interpretation (the flash-model agent's contribution)

The less-intelligent agent, dispatched deliberately without our session's anchoring, produced three hypotheses and one discriminating protocol that this session then executed:

1. **Probe mechanics against a single-slot service** (concurrency + session): partially confirmed as documented traps, but REFUTED as our cause by today's serialized FORCE_NEW_SESSION failures.
2. **Per-locale model build state**: REFUTED (es build SUCCEEDED throughout the failure window; no es rebuild in days).
3. **The one-shot phrasing itself** (deterministic per locale): REFUTED at the invocation layer (both names fail identically with IntentForDifferentSkill; the failure precedes payload parsing).

Its discriminating protocol ("rerun with FORCE_NEW_SESSION serialized, then again 30+ minutes later with no rebuilds") is exactly what produced today's decisive evidence. The de-anchoring worked: the concurrency constraint (finding 5) and the non-diagnosticity precedent (finding 4) were NOT in my session's prior analysis.

## Confidence Assessment

- **HIGH**: the failure is upstream of our skill (routing-layer consideredIntents, name-independent, session-independent, content-independent per the old-model check, builds green, profile-nlu green for the same locales); the documented locale subset is stale; no vendor status page exists.
- **MEDIUM**: whether the current window is an "outage" (implies recovery) vs a longer-lived per-locale simulator regression (the 2018 es-ES precedent had no public resolution). The 2026-09-21 morning passes prove the window has edges (it worked, then it did not), favoring a recoverable degradation.
- **Refuted by this research**: our-protocol bug (the six-class audit + zero error-type session ends), model content (old-model A/B), build state (green throughout), session carryover (FNS still fails), concurrency (serialized still fails).

## Practical protocol (already saved to memory, reinforced)

Before diagnosing any simulate failure as model/code: run the it-IT + en-US evergreen one-shot controls; if they pass and the target locale fails with IntentForDifferentSkill consideredIntents, it is this class; do not touch the model. The recovery poll (hourly, JF-551 gate) is the correct automated response. NEW discriminator from this research: check `consideredIntents` for `<IntentForDifferentSkill>` on failure; it cleanly separates invocation-layer failure (their side) from payload NLU miss (our model).

## Sources

1. https://developer.amazon.com/en-US/docs/alexa/smapi/skill-simulation-api.html - official Skill Simulation API reference (locale list, session.mode; retrieved 2026-09-22)
2. https://developer.amazon.com/it-IT/docs/alexa/smapi/skill-simulation-api-v1.html - v1 reference (concurrency constraint, 409/429/503 semantics)
3. https://stackoverflow.com/questions/59058251/ask-cli-simulate-not-resolving-skill-returning-intentfordifferentskill-error - 2019: IntentForDifferentSkill + force-new-session workaround
4. https://stackoverflow.com/questions/60872367/problem-with-ask-alexa-skill-kit-cli-ask-deploy-and-ask-dialog-simulate-creat - 2020: same error text with a skill-side root cause (non-diagnosticity)
5. https://stackoverflow.com/questions/79992022/custom-dev-skill-any-invocation-phrase-fails-with-not-supported-on-this-device - 2025: upstream invocation-layer failure confirmed via CloudWatch
6. https://github.com/alexa/alexa-skills-kit-sdk-for-nodejs/issues/420 - 2018: es-ES simulator locale defect (historical)
7. https://github.com/alexa/ask-toolkit-for-vscode/issues/62 - 2020: VSC simulator vs SMAPI surface divergence (bare-open "undefined response")

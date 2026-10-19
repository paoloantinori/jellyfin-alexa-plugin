---
id: JF-877
title: >-
  JF-878: SkipForwardBack wrapper-tail twins for fr/es/nl, gated on those
  locales gaining wrapper compositions
status: Done
assignee: []
created_date: '2026-10-12 21:48'
updated_date: '2026-10-19 23:53'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From JF-875's execution (worker draft, renumbered to avoid the JF-876 collision): SkipForwardBack one-shot wrapper-tail twins for the fr/es/nl payload shapes. GATED: no working one-shot wrapper compositions exist for those locales today (their wrapper payloads differ from it-IT's Di-form tail), so the twins cannot be probed until a wrapper composition lands; the en family needs none (its wrapper payload IS the bare imperative, already covered). Revisit when any fr/es/nl wrapper family gets built (the JF-844-class discipline then applies: bare-infinitive literal rows verified probe-first).
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
DISPATCH 2026-10-19 23:10 (orchestrator), the FR LEG ONLY (es/nl stay gated: still no wrapper families there): the gate condition 'revisit when any fr wrapper family gets built' is MET since JF-900/JF-907 (live-composed 'demande a mon serveur de + infinitive' one-shots route). PROBE-FIRST BASELINE (profile-nlu, skill 33dfacd5, 2026-10-19 23:05): 'demande a mon serveur de sauter en avant de trente secondes' -> SkipForwardBackIntent (the AMOUNT-carrying composed form routes TODAY via trainer generalization from the JF-875 base rows); 'saute en avant de trente secondes' -> SkipForwardBackIntent (bare base, healthy); 'demande a mon serveur de sauter en arriere' -> AMAZON.FallbackIntent 3/3 fr-FR AND 1/1 fr-CA (the DIRECTION-ONLY wrapper twin is the condemned row; the missing it-IT 'saltare {seek_direction}' equivalent). CHANGE SHAPE: add the two wrapper-tail rows to SkipForwardBackIntent in templates/fr-FR.yaml AND templates/fr-CA.yaml, mirroring the it-IT saltare twins (it-IT.yaml ~1022-1030): 'sauter {seek_direction}' (the fix) + 'sauter {seek_direction} de {seek_amount} {seek_unit}' (the explicit twin pinning the already-routing amount form); extend the JF-875 block comment with the JF-877 note + probe map. ACCEPTANCE (orchestrator, post-deploy): the direction-only composed form routes SkipForwardBackIntent with seek_direction filled, both fr locales; the amount form stays green; no regression on the JF-875 base rows or the transport-steal guards (no bare direction rows added).

FR LEG SHIPPED 2026-10-20 00:15 (orchestrator): merged 9f2ecf6d (worker b1a5bab4 + in-session review fixes 035f2e28), CI+CodeQL green, deployed (md5 7c789e7a, dir AlexaSkill_1.0.1.0 - NOTE the catalog auto-update pulled release 1.0.1.0 between the JF-876 deploy and this one, creating the new versioned dir and displacing the JF-876 hot-swap; this deploy's DLL carries BOTH JF-876 code and the fr models, so both are live again), fr-FR + fr-CA rebuilt via the plugin endpoint (both SUCCEEDED). ACCEPTANCE GREEN (profile-nlu live): the condemned 'demande a mon serveur de sauter en arriere' routes SkipForwardBackIntent seek_direction filled on BOTH locales; the amount word-form and digit-form fill all three slots (trente and 30 both yield seek_amount 30); the bare regression holds. The code-review F1 fix (the prefixed 'de sauter {seek_direction}' row) was load-bearing: the bare-infinitive twins alone would have left the wrapper on generalization.

F2 CORRECTION (2026-10-20 00:25, code-review finding 2): the dispatch note's claim 'es/nl stay gated: still no wrapper families there' was WRONG. es-ES has carried a live-verified wrapper family since JF-551 (subjunctive 'reproduzca' rows, pinned in es fixtures + the battery); nl-NL carries om-te wrapper tails. PROBE BASELINES with the CORRECT invocation names (mi coleccion / mijn collectie): es-ES 'pide a mi colección que salte hacia atrás' Fallback AND its amount form Fallback (bare 'salta hacia atrás' routes); nl-NL 'vraag mijn collectie om vooruit te springen' Fallback AND its amount form Fallback (bare 'spring 30 seconden vooruit' routes). Both legs are probe-condemned and DISPATCHED (same night).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
SHIPPED 2026-10-20 02:00, ALL THREE LEGS (fr + es + nl; the task's full scope). fr: merged 9f2ecf6d, deployed, models rebuilt, acceptance green (composed direction-only/word/digit all route with slots filled on both fr locales). es/nl: merged eb9fa4aa (worker b36691aa + in-session rounds 4d99ed85, c801a6cd), deployed (md5 5ede0a69), es-ES/es-MX/es-US/nl-NL models rebuilt SUCCEEDED. ACCEPTANCE: es-ES composed forms ALL GREEN (direction-only, word amount, digit amount-first, bare regression; 'hacia atrás' fills). nl-NL GREEN (composed direction-only + word amount; bare regression). es-MX/es-US: the saved models carry the mirror-identical 9 rows and the BARE payloads ('que salte hacia atrás') route, but the COMPOSED one-shots stay Fallback after an identical-content rebuild - invocation-layer trainer state for those two locales (the documented es invocation-layer class), parked with their composed fixture pins standing RED as trackers (the JF-769 ja precedent); re-probe on the next unrelated-content rebuild. BONUS FIX shipped in the lane: 'hacia atrás' was vocabulary-foreign (SeekVocabulary.IsBackward exact-membership) and would have sought FORWARD on a backward ask - fixed at the type layer (SeekDirection synonyms in the es trio + the BackwardWords twin; 6087 tests, the +2 mirror-guard rows auto-derived). Also landed: the es-MX/es-US SkipForwardBack family converted to the JF-855 declared mirror (output-neutral, models byte-identical), the locale-keyed battery outage controls (the it-IT-only control made non-it simulate rows structurally vacuous), the fixture invocation_name parity lint (17 NLU + 20 e2e files, mismatch + deletion, negative-tested; the JF-558 month-lag class), and JF-912 filed (derive the composer name from the model, retiring the three-copy invariant). Follow-ups: JF-912; the JF-855 note gains the conversion data point.
<!-- SECTION:FINAL_SUMMARY:END -->

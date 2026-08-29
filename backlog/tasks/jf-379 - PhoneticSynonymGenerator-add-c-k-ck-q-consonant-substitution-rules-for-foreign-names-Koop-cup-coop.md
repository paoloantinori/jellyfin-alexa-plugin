---
id: JF-379
title: >-
  PhoneticSynonymGenerator: add c/k/ck/q consonant-substitution rules for
  foreign names (Koop->cup/coop)
status: To Do
assignee:
  - zai
created_date: '2026-07-25 18:07'
updated_date: '2026-08-29 08:00'
labels:
  - enhancement
  - phonetic
  - asr
  - artist-search
  - catalog
  - designed
  - multi-session
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PhoneticSynonymGenerator (Alexa/Catalog/PhoneticSynonymGenerator.cs) currently handles: whole-word overrides (soul->sol), the -ing->-in tail rule, and an intervocalic consonant doubler. It has NO c/k/ck/q consonant-substitution rules.

User insight (2026-07-25, Koop debugging): an it-IT Echo transcribed the artist 'Koop' as 'cup' (natural pronunciation) and 'coop' (when spoken with Italian vowel sounds). The c/k/ck family is a well-documented Romance-L1 ASR confusion: Italian speakers render foreign /k/ unpredictably, and Italian's native 'qu' pattern means /k/ can land as 'q' too (quop). Adding c<->k<->ck<->q substitution rules would cover a large family of foreign artist/album names, not just Koop.

This is the same coverage goal the existing machinery serves (emit enough plausible variants that one matches ASR output). The rules belong alongside the existing Romance tail rules / GetRomanceConsonantVariants.

NOTE: distinct from the catalog-injection question (JF-380). This task is about generating variants; whether they reach the device depends on the catalog being correctly populated per locale.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Add c/k/ck/q consonant-substitution rules to PhoneticSynonymGenerator.ApplyRomanceTailRules (or a new consonant-variant step) so an English name like 'Koop' emits variants covering ASR's Romance-L1 transcription drift: Coop, Cop, Cup, Quop, Ckop, etc.
- [ ] #2 Bound the variant count per name (consistent with the existing per-name cap, currently 5) so coverage doesn't explode the catalog/slot size; device-captured forms ordered first
- [ ] #3 Unit tests: given 'Koop', the generator emits at least one of cup/coop/cop; given a name with no k/c, no spurious variants
- [ ] #4 Live verify: re-sync catalog, confirm the JellyfinArtist catalog version for 'Koop' includes the new phonetic variants; on-device 'suona koop' resolves (manual)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
REOPENED-BY-MISTAKE 2026-08-29: briefly set In Progress based on the stale AC#1 ('add c/k/ck/q rules'); the 2026-07-27 REDESIGN supersedes that AC - data-driven generative composite (Epitran port + PHOBLE interference maps + inverse orthography), explicitly multi-session with open questions awaiting maintainer confirmation ('Confirm the team wants to commit', spec question 4). Reverted to To Do untouched. Before ANY implementation: the open questions in docs/superpowers/specs/2026-07-27-jf379-generative-phonetic-synonyms-design.md need decisions (CMUdict delivery form, replace-vs-alongside transition, rollout flag).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
REDESIGNED 2026-07-27 from hand-rolled rules to a data-driven GENERATIVE composite, per maintainer requirement (no per-case hand substitutions; must generalize across languages).

Research (claudedocs/research_jf379_generative_phonetic_libs_2026-07-27.md, exhaustive, primary sources): NO turnkey library GENERATES L1-transfer accent variants. The build is a composite:
1. Forward English g2p (text -> IPA): Epitran rule engine (MIT, port to C#; Go port exists as reference) + CMUdict (public domain) for English-word coverage. Spot-check: CMUdict contains koop/bush/soul/coughing/pink/floyd/adele/metallica/nirvana; misses novel coinages (radiohead) which fall back to rules.
2. Per-L1 phonological interference (the core win): a once-curated map from English phonemes to L1-substituted phonemes, DERIVED FROM PHOIBLE 2.0 feature vectors (CC-BY). Replaces per-case hand rules with one feature-distance table per L1.
3. Inverse orthography (per-L1 IPA -> spelling): necessarily per-L1, small static table.

SCOPE: only the 7 L1s the plugin already has generators for (it/de/es/fr/pt/ja/nl), bounded to the user's catalog vocabulary. NOT all languages.

Design spec: docs/superpowers/specs/2026-07-27-jf379-generative-phonetic-synonyms-design.md (committed 814e93b). Multi-session build; spec captures open decisions for review (CMUdict delivery: full vs trimmed-per-catalog vs rule-only; interference curation method; rollout gating behind a feature flag).

NOT YET IMPLEMENTED. Status remains To Do (designed, awaiting build). Related: JF-381 (query-time Double Metaphone, shipped, fixes the reported Koop/cup defect at the query layer; JF-379 is the catalog-layer one-shot complement).
<!-- SECTION:FINAL_SUMMARY:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

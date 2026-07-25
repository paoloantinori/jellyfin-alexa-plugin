---
id: JF-379
title: >-
  PhoneticSynonymGenerator: add c/k/ck/q consonant-substitution rules for
  foreign names (Koop->cup/coop)
status: To Do
assignee: []
created_date: '2026-07-25 18:07'
labels:
  - enhancement
  - phonetic
  - asr
  - artist-search
  - catalog
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/PhoneticSynonymGenerator.cs
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

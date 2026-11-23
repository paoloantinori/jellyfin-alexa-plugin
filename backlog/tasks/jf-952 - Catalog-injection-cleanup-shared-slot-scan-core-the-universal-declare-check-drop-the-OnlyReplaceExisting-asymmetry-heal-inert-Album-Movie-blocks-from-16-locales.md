---
id: JF-952
title: >-
  Catalog injection cleanup: shared slot-scan core + the universal declare check
  (drop the OnlyReplaceExisting asymmetry, heal inert Album/Movie blocks from 16
  locales)
status: To Do
assignee: []
created_date: '2026-11-23 19:50'
labels:
  - catalog
  - cleanup
  - jF-946-followup
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogManager.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two followups from the JF-946 post-release simplify/altitude round (2026-11-23), both in CatalogManager.cs's injection path:
(1) SHARED SLOT-SCAN CORE (reuse finding, low severity): AnyIntentSlotDeclaresType (the read-only predicate) and UpdateSlotTypesInIntents (the JF-332 mutator, ~line 1249) are the only two ["slots"] walks in the Alexa/ tree and duplicate the same intents-array to slots-array to slot["type"] traversal (~20 lines). Extract a shared per-slot match core (count-or-any) both call, per the ONE-definition doctrine. Do NOT touch the mutator's behavior (JF-332 seam); the extraction is mechanical.
(2) UNIVERSAL DECLARE CHECK (altitude finding, the deeper fix): the current asymmetry is grandfathered, not principled: five types add-or-replace anywhere, song requires declaration. The clean rule: an unreferenced catalog block buys nothing yet counts against the same 1.5MB effective-model cap (the inert SongTitle block pushed ja-JP/fr/pt-BR/nl-NL over), so song is merely the first type big enough to trip a liability every add-anywhere type already carries. The predicate: wire any type only where AnyIntentSlotDeclaresType(slotTypeName) || (replacesType != null and AnyIntentSlotDeclaresType(replacesType)), checked before the ADD branch (keep replace-in-place for existing blocks so a locale that once declared keeps its wiring until an explicit remove). The replacesType disjunct keeps Artist's 11-locale re-typing intact (they declare AMAZON.Musician, the replaced type), no-ops Series/Audiobook (declared everywhere), and drops the inert Album/Movie blocks from 16 locales: a per-locale size heal for every synced model. Needs its own verification pass (changes live album-leg behavior inside the JF-332 seam); the OnlyReplaceExisting bool then disappears.
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

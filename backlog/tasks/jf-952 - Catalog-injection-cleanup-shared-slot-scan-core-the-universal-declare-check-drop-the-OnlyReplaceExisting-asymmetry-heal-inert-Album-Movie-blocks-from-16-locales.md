---
id: JF-952
title: >-
  Catalog injection cleanup: shared slot-scan core + the universal declare check
  (drop the OnlyReplaceExisting asymmetry, heal inert Album/Movie blocks from 16
  locales)
status: Done
assignee: []
created_date: '2026-11-23 19:50'
updated_date: '2026-11-24 19:45'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
ATTEMPTED AND DEFERRED (2026-11-24): the universal declare check was implemented (add-branch only: skip new blocks for undeclared types, keep existing blocks) and builds clean, but 10 tests across 4 files fail because their test models carry no declaring intents. The affected tests: CatalogWiringGraftTests (4: FreshModelJson/LiveModelJson builders need PlayMovieIntent+PlayBookIntent declaring slots), CatalogManagerTests (2: bare no-intent models need declaring intents for the types being injected), plus the movie and audiobook model-builder helpers. REVERTED to the shipped song-only check (proven and deployed). TO COMPLETE in a dedicated pass: (1) update FreshModelJson/LiveModelJson in CatalogWiringGraftTests to include PlayMovieIntent with MovieTitle slot and PlayBookIntent with AudiobookTitle slot; (2) update the bare test models in CatalogManagerTests to include declaring intents; (3) re-apply the universal check (the code is in this session's transcript); (4) run the full catalog families; (5) deploy and verify the inert Album/Movie blocks are skipped on add (existing blocks stay). The shared slot-scan core extraction is also pending (AnyIntentSlotDeclaresType vs UpdateSlotTypesInIntents duplicate the traversal).

Shipped 2026-11-24 via the merge of worktree-agent-a6f11a6e4cc39af2d (base 651d3ccb + review round 5de5e9ce). The universal declare check (AnyIntentSlotDeclaresType(slotTypeName) || declares(replacesType), computed before the JsonObject construction and before the replace decision) gates all six synced types; the OnlyReplaceExisting asymmetry is gone; VisitIntentSlots is the ONE intents-to-slots traversal shared by the predicate and the JF-332 mutator. The refined heal (the review round): IsCatalogWired inspects the existing block's shape (a JsonObject valueSupplier, the ExtractWiring definition), so NOT-declared + WIRED blocks are REMOVED (the JF-946 heal generalized, restoring the over-cap exit my review found missing: a stale block pushing a locale past the 1.5MB effective cap is shrunk on the next sync instead of failing every PUT forever), NOT-declared + STATIC seed blocks are left byte-untouched (never converted to a supplier: the seed-swap finding fixed), declared types replace-in-place. Recoverability documented: a future template declaring the slot re-earns wiring on the next sync. The post-loop summary distinguishes wired/kept-static/removed for over-cap triage. Test coverage includes the 17-locale theory against a hardcoded roster (AlbumName/MovieTitle only it-IT, SongTitle it-IT+de-DE) and the two shape tests (wired-undeclared removed; static-undeclared untouched). 6227/6227 both TFMs; merged main builds clean. Gates: worker ran /simplify (4 agents) + /code-review high (5 findings applied) in its session; orchestrator ran Skill(code-review) medium on the delta whose design-blocking finding (the missing over-cap exit) produced the refined rule the review round implements.
<!-- SECTION:FINAL_SUMMARY:END -->

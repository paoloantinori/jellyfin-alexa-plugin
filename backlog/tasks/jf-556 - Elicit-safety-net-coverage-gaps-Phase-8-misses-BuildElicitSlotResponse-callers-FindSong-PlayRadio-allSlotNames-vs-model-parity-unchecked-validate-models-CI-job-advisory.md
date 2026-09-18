---
id: JF-556
title: >-
  Elicit safety-net coverage gaps: Phase 8 misses BuildElicitSlotResponse
  callers (FindSong/PlayRadio), allSlotNames-vs-model parity unchecked,
  validate-models CI job advisory
status: Done
assignee: []
created_date: '2026-09-13 11:20'
updated_date: '2026-09-18 23:16'
labels:
  - reliability
  - tech-debt
  - interaction-model
dependencies: []
references:
  - JF-550
  - scripts/validate_interaction_models.py
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-550 high-effort review (2026-09-13): three related gaps in the mechanical net guarding the Dialog.ElicitSlot failure modes. All verified against the uncommitted JF-550 diff; none is a live bug today, each is a silent-breakage risk the net currently misses.

1. Phase 8 regex blind spot (scripts/validate_interaction_models.py, check_elicit_dialog_registration): the docstring claims "every intent the plugin elicits must be dialog-registered", but the regexes only match BuildDialogElicitResponse(...) and raw ElicitSlotDirective(...) call sites that spell an IntentNames.* constant. The BaseHandler.BuildElicitSlotResponse callers escape it: FindSongIntent (all ElicitAnswer sites via the FindSong-private wrapper - the intent that ORIGINALLY hit anti-pattern #9 live on 2026-08-21) and PlayRadioIntent (BuildStationElicit). Both are registered today so Phase 8 stays green, but a future removal of either dialog entry would break silently with the checker green. Fix: add a third pattern, BuildElicitSlotResponse\([^;]*?IntentNames\.(\w+), which matches both current call shapes (ElicitAnswer passes IntentNames.FindSongIntent; BuildStationElicit passes IntentNames.PlayRadio first).

2. allSlotNames parity is unchecked anywhere: Amazon rejects a partial updatedIntent with INVALID_RESPONSE "All slots must be defined..." (live 2026-08-28, ElicitSlotDirective docstring), but neither the unit tests (AssertElicitsSlot checks SlotToElicit + intent name only) nor Phase 8 (checks dialog-entry slots vs languageModel slots) compares the handler's allSlotNames argument list against the model's slot set. Adding a slot to an intent and forgetting the elicit params passes every gate and fails only live. Parity today is correct at all 15 BuildDialogElicitResponse sites plus FindSong/PlayRadio (reviewer-verified). A source-grep of the params lists vs model slots is feasible in the same validator (brittle but this repo already greps handler source there).

3. The validate-models CI job is continue-on-error (advisory, pre-existing arrangement for the it-IT SearchQuery warning), so Phase 8's error level does not actually gate CI. JF-550's notes already contemplate promoting the question detector to --strict in CI; decide both promotions together so the anti-pattern #9 class (silent directive drop) has at least one hard gate.
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
Shipped in 1d1579d5 (validator + CI only; no DLL change, so no deploy needed beyond the JF-593 build already live). Items: (1) the BuildElicitSlotResponse third pattern was already in Phase 8 from the prior session; re-verified live via the dialog-entry-removal red proof. (2) allSlotNames parity now covers ALL four statically resolvable call shapes through one consolidated _parity helper: inline string lists, IntentNames.Slots constant-token arrays (closes the PlaySong/PlayAlbum sweep sites the JF-550 review could not see), direct BuildElicitSlotResponse constant arrays (PlayRadio), and the FindSong wrapper identity funnel; silent-vanish paths (Slots class not found, wrapper funnel drift) emit warnings instead of passing quietly. All four red paths proven live (PlaySong token swap, PlayRadio array mutation, FindSong wrapper slot swap, dialog-entry removal), green restored. (3) validate-models CI job promoted from continue-on-error to blocking: the advisory stance existed for the it-IT SearchQuery violation, which no longer exists as an error; the validator fails CI on errors only, warnings never fail it, so warning-level checks (incl. #10 bare-album-carriers, which CLAUDE.md conditioned on this promotion) keep their BY-DESIGN severity - that conditional was resolved and documented in the same change. Review cycle: code-review (4 cleanup angles + correctness) returned 5 findings + 1 sibling-shape recommendation; all applied in-change. Suite 4125/4125 both TFMs (no C# change), validator PASS (118 non-fatal warnings).
<!-- SECTION:FINAL_SUMMARY:END -->

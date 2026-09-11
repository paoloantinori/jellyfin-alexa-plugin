---
id: JF-316
title: >-
  Extend the YAML interaction-model generator to all 17 locales (kill
  hand-edited JSON drift)
status: In Progress
assignee: []
created_date: '2026-07-12 14:58'
updated_date: '2026-09-11 13:47'
labels:
  - maintainability
  - interaction-model
  - tech-debt
milestone: m-7
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
  - scripts/generate_interaction_model.py
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Only it-IT is generated from a template (`Alexa/InteractionModel/templates/it-IT.yaml` via `scripts/generate_interaction_model.py`); the other 16 `model_*.json` are hand-maintained (en-US 46KB, it-IT 65KB). Adding one intent means editing 16 large JSON files by hand. The project's own CLAUDE.md documents the direct consequences: "Cross-Locale Drift (8+ incidents)" and "Static Samples Without Slots (7+ incidents)" — recurring regressions the generator already prevents for it-IT but was never extended. Architecture review 2026-07-12 flags this as the second strategic debt alongside the God class.

Fix: extend the YAML-template generator to all 17 locales (per-locale vocabulary files + shared structure), so a new intent/slot is authored once per locale in YAML and generated. Keep the existing validators (validate_interaction_models.py) as the guardrail. This is larger than it looks (each locale needs its own vocabulary curation) — consider a parent task with per-locale-group subtasks, starting with the locales that share structure.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The generator produces model JSON for all 17 locales from per-locale YAML templates
- [ ] #2 Regenerating all locales reproduces the current committed models (or diffs are reviewed and intentional) for a clean baseline
- [ ] #3 validate_interaction_models.py passes on all generated models
- [ ] #4 Documentation updated: adding an intent is a YAML edit + regenerate for every locale, not hand-editing JSON
- [ ] #5 The it-IT generator path continues to work unchanged
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
JF-415 addendum (2026-09-10, /simplify gate): JF-415 added a JellyfinArtist static seed block to 6 model files (5 identical en-* blocks + it-IT via the YAML template). When the YAML generator extends to all 17 locales, OWN this seed from the shared table. Interim option: a warning-level check in validate_interaction_models.py for en-* JellyfinArtist seed equality (the 5 identical blocks can drift silently with no cross-check today).

Review finding 2026-09-11 (milestone-1 gate, en-US golden master): scripts/generate_mood_slot.py still rewrites model_en-US.json directly (its LOCALE_MOODS en-US table replaces the whole Mood type and filters PlayMoodMusicIntent samples), so the template and the mood script are two competing writers for the same model file. templates/en-US.yaml's header carries an interim warning and defers consolidation to 'a later JF-316 milestone'. This note is that milestone's owner: a later milestone must absorb the English mood table into en-US.yaml (and the per-locale tables into their templates) or delete/downscope generate_mood_slot.py, before any locale regen is trusted after a mood-table edit.

MILESTONE 1 COMPLETE (2026-09-11, merge 1b2c115e): en-US joins the YAML architecture with a BYTE-IDENTICAL golden master (regen reproduces the committed model exactly - verified by worker, orchestrator, and gate independently with cmp). Generator gains: the ordered-intents template form (bare string = name-only; verbatim samples / vocabulary templates / slots), key-order contracts (intent keys + dict type values in YAML order), prompts + modelConfiguration passthroughs, and - from the gate's findings, the load-bearing hardening for the 15 golden-master-less locales ahead - the unknown-key guard extended to the LEGACY sections (typo = hard error, not silent sample-drop), {ref} validation against vocabulary-sets-union-declared-slots on EXPANDED strings (catches typo'd template refs and vocabulary-VALUE-introduced refs), dict type-value key guards, and both-forms raise. it-IT normalized value-first (output-neutral) and its path unchanged (AC#5 verified). Ten-bad-template negative battery green. Template shape: honest transcription first (61 intents, 15 bare, 42 verbatim, 5 product blocks / 8 vocab sets, 64/552 samples from products) - vocabulary extraction only where real patterns exist. NO DEPLOY NEEDED (models byte-identical). MOOD-CONFLICT tracked (gate finding): generate_mood_slot.py and the en-US template are two writers of the same Mood block; they agree today but a future LOCALE_MOODS edit + mood-script run regresses on the next regen - milestone-2 scope (consolidate the mood tooling into the templates) along with a regen-equality warning check in the validator. NEXT: milestone 2 = en-GB/AU/CA/IN (shared structure, identical seed block) + the mood consolidation.

Addendum disposition (2026-09-11, JF-316 milestone 2): the interim option is now IMPLEMENTED as a warning folded into the validator's Phase-5 template-regen walk (check_template_regen_equality hashes each templated locale's JellyfinArtist values and warns when the en-* family members disagree; it-IT's 8-value block legitimately differs, so the comparison is en-family-scoped and needs no exemption list). The full fix remains "own the seed from the shared table"; this sub-check retires naturally when that lands.
<!-- SECTION:NOTES:END -->

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

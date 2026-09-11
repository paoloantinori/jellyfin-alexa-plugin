---
id: JF-316
title: >-
  Extend the YAML interaction-model generator to all 17 locales (kill
  hand-edited JSON drift)
status: Done
assignee: []
created_date: '2026-07-12 14:58'
updated_date: '2026-09-11 19:14'
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

MILESTONE 2 COMPLETE (2026-09-11, merge a5efab87): the en-* family (en-GB/AU/CA/IN) templated with byte-identical golden masters - six of seventeen locales now regenerate from YAML (en-US/GB/AU/CA/IN + it-IT; worker + gate + orchestrator cmp-verified independently). The mood two-writers conflict RESOLVED by writer-scoping: LOCALE_MOODS lost its templated-locale entries (census-proven no other consumers), generate_mood_slot.py refuses templated locales via the generic file-existence check (auto-extends; delete-at-17 noted), a mood-word change is a template edit + regen for the family. validate_interaction_models.py Phase 5: auto-discovering regen-equality warning (255ms/6 locales, fix-command + first-divergence in the message, Exception-broadened after the gate's crash-gap repro) + the JF-415 seed addendum IMPLEMENTED as the JellyfinArtist en-family seed-equality warning inside the same walk (retires at the shared table). serialize_model() is the ONE serialization contract (generator + validator share it). Gate's 8 findings all applied (header count fixes, CLAUDE.md's two stale it-IT-only spots, count-free docstrings, the LocalizedMoodMap it-accuracy). NO DEPLOY (models unchanged bytes). REMAINING: 11 locales - milestone 3 = de-DE + es-ES/es-MX/es-US; milestone 4 = fr-FR/fr-CA + pt-BR; milestone 5 = ja-JP/hi-IN/ar-SA/nl-NL; then AC#4 (docs: adding an intent = YAML edit + regen) and the shared-seed-table endgame.

MILESTONE 3 COMPLETE (2026-09-11, merge 6c0f8395): de-DE + es-ES/es-MX/es-US templated - TEN of seventeen locales now regenerate byte-identically. ZERO generator changes (the milestone-1 machinery held: built-in Musician slots, de-DE's custom loop intents + modelConfiguration, numeric Decade synonyms). Gate CLEAN (every header count numerically verified; the mood tables' content reconstructed from git history and matched before deletion; the refusal proven by synthetic-entry injection for all 10). Gate archaeology: es-US's ENGLISH FindSong sets are inertia from 036f7eae, never a market decision, never documented - now in the template header + the open question lands on JF-399. LOCALE_MOODS at its final 7 keys. NEXT: milestone 4 = fr-FR/fr-CA + pt-BR; milestone 5 = ja-JP/hi-IN/ar-SA/nl-NL; then AC#4 + the shared-seed table.

MILESTONE 4 COMPLETE (2026-09-11, merge ee8148bc): fr-FR/fr-CA/pt-BR templated - THIRTEEN of seventeen. Second consecutive zero-generator-change milestone; all 13 regen byte-identical (gate proved it in an isolated sandbox with the worktree's own generator). fr-CA = fr-FR minus exactly five in-order-subsequence removals (gate-verified mechanically); pt-BR the divergent one (es-style loops, TimePeriod within-batch-unique, the inert orphan prompts documented not cleaned). Gate PASS, its 2 doc findings applied (the false TimePeriod-uniqueness claim; the 59-vs-61 family counts). JF-542 filed (the orphan-prompts cross-locale state). LOCALE_MOODS at its final 4 keys. FINAL MILESTONE REMAINS: ja-JP/hi-IN/ar-SA/nl-NL (non-Latin scripts, RTL ar - transcription care, NOT normalization), then AC#4 (docs) + the shared-seed table + delete generate_mood_slot.py per its docstring.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-11 (COMPLETE - five milestones + the close-out /simplify pass; merges 1b2c115e / a5efab87 / 6c0f8395 / ee8148bc / b3515737 + cleanup 9d1be575): ALL 17 locales regenerate BYTE-IDENTICALLY from per-locale YAML templates. AC#1 (17/17), AC#2 (the golden master: zero model diffs on full sweeps, cmp-proven by worker+gate+orchestrator at every milestone), AC#3 (validators PASS, 101-warning baseline unchanged), AC#4 (CLAUDE.md final state: JSONs are build output, YAML edit + regen, enforcement = generator guards + Phase-5), AC#5 (it-IT unchanged, byte-proven). generate_mood_slot.py DELETED with its tables provably preserved; the close-out /simplify (Skill-invoked, two four-angle agents) deleted the dead slot_samples machinery (both agents independently), consolidated the tripled guards, single-sourced MODELS_DIR, hardened the both-forms guard, and fixed the stale docstring - all 17 byte-identical after. ENDGAME NOTE (the deliberately-remaining follow-ups): (1) the shared JellyfinArtist seed table (retires the interim en-family warning); (2) it-IT legacy-form retirement - the close-out agent PROVED a mechanical byte-identical conversion to the ordered form exists (94,235 bytes equal), retiring ~60 generator lines + the mixing guard; (3) candidate promotions: Phase-5 regen mismatch warning->error and cross-locale drift routing to errors (release-build.yml is the blocking surface; promote together with encoding the 101-warning wall's documented exemptions - 80 deliberate it-IT-only types, 11 JellyfinArtist gaps on the 11 unswapped locales per JF-415 AC#4, 10 TimePeriod gaps); (4) validate_model.sh's stale locale list (pre-existing). Follow-ups born from the work: JF-542 (orphan prompts), the JF-399 English-inertia consolidation (5 locales + hi-IN Decade values). The July architecture review's second strategic debt is retired: the cross-locale-drift class is mechanically prevented.
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

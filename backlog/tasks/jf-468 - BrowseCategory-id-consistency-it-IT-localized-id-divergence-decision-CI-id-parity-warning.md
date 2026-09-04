---
id: JF-468
title: >-
  BrowseCategory id consistency: it-IT localized-id divergence decision + CI
  id-parity warning
status: Done
assignee: []
created_date: '2026-09-03 09:30'
updated_date: '2026-09-04 18:14'
labels: []
dependencies: []
references:
  - JF-461 (the id backfill this follows)
  - JF-460 (warning-level validator precedent)
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml:671-714
  - 'scripts/generate_interaction_model.py:111-120'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-461 gates (2026-09-03). BrowseCategory slot-value ids now exist in all 17 locales (16 with the English canonical set artists/albums/songs on 3-of-7 values, it-IT with localized ids on all 14), but the invariant lives only as hand-maintained data across scattered lines: nothing in CI validates it and no generator can produce it for the 16 hand-maintained locales (only it-IT is generated; generate_interaction_model.py:111-120 does support id emission from the YAML template).

Carry-forward items, one theme:
1. it-IT id divergence (worker finding, confidence 75): it-IT uses localized ids (artisti, brani, canzoni, film, serie, playlist, ...) while the other 16 locales use English canonical ids. No id string carries two meanings (the only shared string, 'albums', means the same in both), but the 'songs' concept has no shared key with it-IT (split into brani/canzoni/musica) and it-IT carries ids with no counterpart elsewhere (film, serie, playlist, cartoni). Any future id-keyed backend lookup needs an it-IT mapping row regardless of what the 16 locales chose. Note it-IT already mixes conventions: its album value carries the English id 'albums' (model_it-IT.json:2180) while every other it-IT id is localized, so partial English-canonical ids have a toehold. DECISION for the maintainer: either unify it-IT onto the English canonical ids for the shared concepts (YAML template edit lines ~671-714 + regenerate; keeps a uniform key space) or document the it-IT mapping row as permanent. Today nothing reads these ids server-side (GetCanonicalSlotValue reads Name only), so the decision is not urgent.
2. CI guard (fold of the worker's below-threshold validator note): validate_interaction_models.py is id-blind (validate_slot_types_cross_locale compares type NAMES only). A warning-level check that BrowseCategory id presence matches the English 3-of-7 set in the 16 non-it-IT locales would catch future drift; keep it warning-level per the JF-460 precedent (90-warning baseline contract).
3. Cross-type namespace note (pre-existing, no action unless ids become load-bearing): LibraryQueryType ids the same concepts with different strings ('tracks' vs BrowseCategory's 'songs'); an id consumer must not assume one namespace across slot types.

Acceptance criteria:
- Maintainer decision recorded on it-IT (unify via template regen, or document the mapping row in CLAUDE.md near the BrowseCategory notes).
- If the validator check is added: warns on a scratch copy missing an id, silent on the current tree, 90-warning baseline unchanged.
- No behavioral change; suite and validators green.
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
Closed complete: implemented, deployed, live-verified (commit 6bc03c53).

The maintainer decision landed as the UNIFY direction: it-IT's shared-concept BrowseCategory values carry the English canonical ids (artisti->artists, brani->songs; brani chosen because the template's song_noun opens with 'il brano'; albums was already canonical). The 11 it-IT-only concepts keep their localized ids: the unified key space covers only the three shared across the family. No VALUE changed (the regen diff is exactly 2 id lines), so entity resolution and the handler path are untouched (the server reads canonical NAME only). The mapping documented in the YAML template comment block.

The CI guard: a warning-level BrowseCategory id lint (JF-460 precedent). Every locale must carry the shared three; the 16 hand-maintained locales must carry nothing beyond; it-IT's extras deliberately not enumerated. Silent on the current tree (the exact 90-warning baseline holds); the reviewer verified the missing/extra/wrong-id shapes empirically on mutated copies, and confirmed the id mapping by a direct dump of all 17 models (all 16 hand-maintained locales carry exactly artists/albums/songs). Known blind spot documented (a crossed id swap passes; presence checked, not attachment; ~35).

Deploy: DLL swapped, config and the PauseKeepsSession flag survived, the it-IT model rebuilt via the locale-scoped endpoint, and the SAVED Amazon-side model verified carrying the unified ids (shared-three present, 14 total ids: 3 canonical + 11 localized extras). The cross-type namespace note (LibraryQueryType 'tracks' vs BrowseCategory 'songs') remains documented in the task as a no-action-until-load-bearing caution.
<!-- SECTION:FINAL_SUMMARY:END -->

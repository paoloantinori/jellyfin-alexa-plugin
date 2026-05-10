---
id: JF-108
title: French (fr-FR/fr-CA) phonetic synonyms for English names
status: Done
assignee: []
created_date: '2026-05-09 07:40'
updated_date: '2026-05-09 08:16'
labels:
  - enhancement
  - fr-FR
  - fr-CA
dependencies:
  - JF-105
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create `FrenchPhoneticSynonyms` in `Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/` with a `Generate(string name)` method that returns phonetic variants for French speakers pronouncing English artist/album names.

French speakers typically pronounce English differently:
- `th` → `z` or `d` (most French speakers say `z`): "The Beatles" → "Ze Beatles"
- `h` → silent (drop entirely at word start, not just silent `h`)
- `sh` → `ch` (French `ch` already sounds like English `sh`)
- `w` → `ou` before vowels, `v` otherwise (e.g., "Wood" → "Oud")
- `ph` → `f` (same as Italian)
- `tion` → `tion` (French already says `sion` naturally)
- `ck` → `k`
- `ee` → `i` (e.g., "Deep Purple" → "Dip Purple")
- Remove "The" prefix and add "Les" variant: "The Beatles" → "Beatles", "les Beatles"

Reference: `PhoneticSynonymGenerator.cs` for Italian (same structure, different transforms).

## Acceptance Criteria
- `FrenchPhoneticSynonyms.Generate(name)` returns 0-3 phonetic variants
- French-origin names detected and return empty list
- Same rules apply for fr-FR and fr-CA (one shared class)
- Wired into `PhoneticSynonymGenerator` dispatch for `fr-*` locales (depends on JF-105)
- Unit tests for transform logic
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

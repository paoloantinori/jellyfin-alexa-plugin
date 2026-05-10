---
id: JF-106
title: German (de-DE) phonetic synonyms for English names
status: Done
assignee: []
created_date: '2026-05-09 07:39'
updated_date: '2026-05-09 08:12'
labels:
  - enhancement
  - de-DE
dependencies:
  - JF-105
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create `GermanPhoneticSynonyms` in `Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/` with a `Generate(string name)` method that returns phonetic variants for German speakers pronouncing English artist/album names.

German speakers typically pronounce English differently:
- `th` → `s` or `z` (e.g., "The Smiths" → "Se Smiths")
- `w` → `v` (e.g., "Pink Floyd" → "Pink Floyd" — already has `v` sound for German)
- `sh` → `sch` is not needed (Germans already say `sch` for English `sh`), but English `j` → `dsch` might help
- Silent `h` at word start: remove (same as Italian)
- `tion` → `tion` (already sounds German), but `sion` → `zion`
- `ck` → `k` (Germans already pronounce `ck` as `k`)
- Remove "The" prefix and add "Die" variant: "The Beatles" → "Beatles", "die Beatles"

Reference: `PhoneticSynonymGenerator.cs` for Italian (same structure, different transforms).

## Acceptance Criteria
- `GermanPhoneticSynonyms.Generate(name)` returns 0-3 phonetic variants
- German-origin names (common German endings/patterns) are detected and return empty list
- Existing Italian transforms unaffected
- Wired into `PhoneticSynonymGenerator` dispatch for `de-DE` locale (depends on JF-105)
- Unit tests for transform logic
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

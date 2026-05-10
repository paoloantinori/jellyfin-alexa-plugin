---
id: JF-107
title: Spanish (es-ES/es-MX/es-US) phonetic synonyms for English names
status: Done
assignee: []
created_date: '2026-05-09 07:39'
updated_date: '2026-05-09 08:16'
labels:
  - enhancement
  - es-ES
  - es-MX
  - es-US
dependencies:
  - JF-105
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create `SpanishPhoneticSynonyms` in `Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/` with a `Generate(string name)` method that returns phonetic variants for Spanish speakers pronouncing English artist/album names.

Spanish speakers typically pronounce English differently:
- `th` → `d` or `z` (Spain) / `t` (Latin America) — use `d` as most common: "The Beatles" → "De Beatles"
- `sh` → `ch` or `x` (e.g., "Fleetwood Mac" — no change needed)
- `j` → `y` or `ll` (Spanish `j` is already different, but English `j` sounds like Spanish `y`)
- `w` → `gu` before front vowels (e/w), `u` otherwise
- Silent `h` at word start: remove
- `tion` → `sion` (Spanish speakers naturally say `sion`)
- `ph` → `f`
- `ck` → `k` or `c`
- Remove "The" prefix and optionally add "Los" variant: "The Beatles" → "Beatles", "los Beatles"

Reference: `PhoneticSynonymGenerator.cs` for Italian (same structure, different transforms).

## Acceptance Criteria
- `SpanishPhoneticSynonyms.Generate(name)` returns 0-3 phonetic variants
- Spanish-origin names detected and return empty list
- Same rules apply for es-ES, es-MX, es-US (one shared class)
- Wired into `PhoneticSynonymGenerator` dispatch for `es-*` locales (depends on JF-105)
- Unit tests for transform logic
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

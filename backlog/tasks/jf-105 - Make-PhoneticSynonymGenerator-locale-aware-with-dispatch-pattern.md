---
id: JF-105
title: Make PhoneticSynonymGenerator locale-aware with dispatch pattern
status: Done
assignee: []
created_date: '2026-05-09 07:39'
updated_date: '2026-05-09 08:05'
labels:
  - architecture
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

`PhoneticSynonymGenerator.GenerateSynonyms()` is called unconditionally from both `LibrarySyncService` and `DynamicEntityBuilder` without any locale context. The Italian phonetic transforms are applied to all locales equally.

Two call sites need updating:
1. `Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs:163` — `CatalogPayload.FromItems(catalogType, itemTuples, PhoneticSynonymGenerator.GenerateSynonyms)`
2. `Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntityBuilder.cs:130` — `PhoneticSynonymGenerator.GenerateSynonyms(item.Name)`

## Architecture Change

1. Change `GenerateSynonyms(string name)` signature to `GenerateSynonyms(string name, string locale)` (or add an overload for backward compat)
2. Inside, dispatch to locale-specific generators based on the locale prefix:
   - `it-*` → existing Italian logic (current `PhoneticSynonymGenerator` body)
   - `de-*` → `GermanPhoneticSynonyms.Generate()` (JF-106)
   - `es-*` → `SpanishPhoneticSynonyms.Generate()` (JF-107)
   - `fr-*` → `FrenchPhoneticSynonyms.Generate()` (JF-108)
   - `en-*` or unknown → return empty list (English names don't need phonetic correction for English Alexa)
3. Update both call sites to pass locale. For `LibrarySyncService`, the locale is available via `ItalianLocale` constant (currently hardcoded to `it-IT` — will need to be parameterized for multi-locale catalog sync). For `DynamicEntityBuilder`, locale comes from the Alexa request.
4. Move the current Italian transforms into `ItalianPhoneticSynonyms` class (or keep inline in the dispatch for `it-*`).

## Acceptance Criteria
- `GenerateSynonyms` accepts locale parameter
- Italian phonetic transforms still work identically for `it-IT` locale
- English locales (`en-*`) return empty synonym lists (no transforms needed)
- Both call sites pass locale
- All existing tests pass
- dotnet build passes
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

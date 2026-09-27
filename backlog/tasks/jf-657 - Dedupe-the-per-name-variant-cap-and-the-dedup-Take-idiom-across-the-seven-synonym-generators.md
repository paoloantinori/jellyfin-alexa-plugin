---
id: JF-657
title: >-
  JF-657 - dedupe the per-name variant cap and the dedup+Take idiom across the
  seven synonym generators
status: To Do
assignee: []
created_date: '2026-09-27'
updated_date: '2026-09-27'
labels:
  - cleanup
  - catalog
priority: low
dependencies: []
references:
  - >-
    backlog/tasks/jf-646 - JF-646-catalog-side-katakana-synonyms-Latin-artist-album-names-get-kana-variants-in-the-ja-catalog-upload-so-NLU-selection-resolves-naturalized-ja-voice-the-routing-layer-complement-to-JF-643.md
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed same-turn from the JF-646 /simplify round (all three reviewers flagged it; none judged it a drive-by for that diff): the per-name synonym cap and the dedup+cap idiom are copy-pasted across the generator family instead of owned once.

Current state (verified by the reviewers, 2026-09-27):
- `PhoneticSynonymGenerator.PerNameVariantCap = 5` (new with JF-646's ja combiner)
- `ItalianPhoneticSynonyms.PerNameVariantCap = 5` (private, same name, second copy)
- French/Spanish/PortuguesePhoneticSynonyms hardcode `Take(5)`
- `JapanesePhoneticSynonyms` self-caps at `Take(3)` inside the now-5-capped ja dispatch (harmless nesting, 3 < 5, but reads as a second policy)
- the `Distinct(OrdinalIgnoreCase).Take(cap).ToList()` idiom appears in six places

THE WORK: one shared internal constant on PhoneticSynonymGenerator (the natural home: it already hosts the shared Romance rules) referenced by all generators, and either a shared Append-Capped helper for the idiom or a documented reason each site keeps its inline form. Behavior must stay byte-identical for every locale (the existing per-locale payload tests pin this).

Not urgent: pure internal consolidation, zero behavior change intended.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-27 origin: JF-646's /simplify reviewers (reuse + quality rounds). Deliberately not done inside JF-646: the task was catalog-side kana generation, and touching four sibling generators' caps is a cross-sibling behavior-adjacent cleanup that deserves its own review pass.
<!-- SECTION:NOTES:END -->

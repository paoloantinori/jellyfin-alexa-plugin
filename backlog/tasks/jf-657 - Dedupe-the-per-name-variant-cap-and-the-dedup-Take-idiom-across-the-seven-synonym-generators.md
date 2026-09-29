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

2026-09-29 DONE (implemented on the agent worktree branch; orchestrator merges): consolidation map.
CONSOLIDATED: (1) the 5 cap is ONE internal const, PhoneticSynonymGenerator.PerNameVariantCap, referenced by Italian, French, Spanish, Portuguese, the ja combiner, and Italian's velar pre-check; Italian's private copy is deleted. (2) PhoneticSynonymGenerator.CappedDistinct(results, cap) is the ONE definition of the Distinct(OrdinalIgnoreCase).Take(cap).ToList() idiom; all 8 exits (it/fr/es/pt/de/nl/ja-romaji/ja-combiner) call it.
KEPT LOCAL: (a) the deliberate 3 at German/Dutch/JapanesePhoneticSynonyms is a literal argument with a pointer comment: it is three arms' own documented, test-pinned contracts ("Generates up to 3"; they emit at most 2/2/1 variants so the cap never truncates). A second shared const was DECLINED: the altitude and reuse reviewers both argued it would promote an inert number to a fake policy and couple three independent arms, and the task says to consolidate only what is genuinely the same constant. (b) KatakanaSynonymGenerator's Distinct(StringComparer.Ordinal) exit stays inline, now with a why-comment (uncapped: the ja combiner owns the 5-cap and the kana-first ordering; kana case never varies). (c) DynamicEntityBuilder.JaDynamicSynonymCap = 2 is a different surface (dynamic entities, not catalog) and is untouched. (d) Italian's velar pre-check stays an inline results.Count compare against the shared constant (a pre-exit skip optimization, not the exit idiom).
Proof: behavior-preserving by construction (same comparer, same insertion order, same cap values); full suite on the final tree 4719/4719 net9.0 + 4719/4719 net10.0, existing tests unmodified; generator/payload filter 389/389 both TFMs. Gates: /simplify four-agent round (2 findings applied: cap-3 rationale deduped to one authoritative home on the constant's doc plus pointer comments, dead using System.Linq removed from JapanesePhoneticSynonyms) and code-review high fork (no correctness bugs; 3 of 4 findings applied: katakana why-comment + helper-doc "every locale generator" overclaim fixed, shared doc's ja-romaji bound corrected to "at most 1", Italian's stale "past index 5" bound replaced with a cap-number-free phrase; 1 declined: the shared second constant, reasoning above).
Deferred, recorded here per the same-turn rule: the comment at Jellyfin.Plugin.AlexaSkill.Tests/Unit/PhoneticSynonymGeneratorTests.cs:403 still reads "the Take(5) cap" although the idiom is now CappedDistinct; fixing it is a one-line comment edit barred by this task's existing-tests-unmodified constraint, take it on the next touch of that file.
<!-- SECTION:NOTES:END -->

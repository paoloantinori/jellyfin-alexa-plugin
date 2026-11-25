---
id: JF-970
title: >-
  nlu_static: sample-level carrier lookup (candidate delta table) so
  model-content premises are tool output
status: Done
assignee: []
created_date: '2026-11-25 16:24'
updated_date: '2026-11-25 17:07'
labels:
  - nlu-tooling
  - process
dependencies: []
references:
  - .claude/skills/nlu-debug-protocol/SKILL.md
  - scripts/nlu_static/matcher.py
  - docs/nlu-static-analysis.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Four model-content premises were wrong on 2026-11-25 (SongTitle "static everywhere"; "no `music by {musician}` row on the artist intent"; "we hold neither artist order"; JF-965's "en carries by-order on FindSong, de/fr/es lack it" while `Spiele Lieder von {musician}`, `Reproduce canciones de {musician}` and `Lis les chansons de {musician}` already sit on PlayArtistSongsIntent). Each was caught only by a consultant re-deriving the fact with inline Python. The existing `match` subcommand cannot serve as the check for candidate SAMPLES: it takes a real utterance and shows a catalog-slotted sample only when the spoken value is in the vocabulary. Verified 2026-11-25: `match --locale en-US "play some jazz"` lists only PlayByGenreIntent and PlaySongIntent, although en-US carries `play some {musician}` (JellyfinArtist) on PlayArtistSongsIntent, so a proposed bare `play some {genre}` would be an identical carrier that the tool does not reveal. Wanted: a subcommand (suggested name `carriers`) that takes candidate samples (inline or a file of locale + intent + sample) and, per (candidate, locale), reports ABSENT, PRESENT on the same intent, or SAME_CARRIER on another intent with that intent's slot name and slot type, using the toolkit's own normalizer and pattern rules with every slot reduced to a placeholder. Deterministic JSON and text output like the other subcommands; `--all` for every locale; ja-JP follows the shared skip convention or a character-level comparison, whichever the toolkit already supports. Process rule this enables (see .claude/skills/nlu-debug-protocol, Gate 1): every task or research report that proposes rows quotes this table, and only ABSENT rows are work.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A candidate sample whose normalized carrier exists on another intent is reported SAME_CARRIER with the owner intent, its sample, slot name and slot type, regardless of whether any real value would match that slot (the en-US `play some {genre}` versus `play some {musician}` case is a unit test)
- [x] #2 A candidate identical to an existing sample of the named intent is reported PRESENT; an unseen carrier is reported ABSENT
- [x] #3 File input accepts many (locale, intent, sample) rows and `--all` expands one candidate across all locales; output is deterministic JSON plus a text table
- [x] #4 docs/nlu-static-analysis.md documents the subcommand and states that `match` is not a presence check for catalog-slotted samples
- [x] #5 The JF-965 candidate list is re-run through the tool and its description corrected to the real delta
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DoD notes: pure-Python tooling change (scripts/nlu_static + tests/scripts + docs); the dotnet/build/test DoD items and the C#-specific ones (session DTOs, HttpClient, locale strings, E2E) do not apply to this delta; the python suites are the test evidence (462 scripts tests green on the merged branch, re-run by the orchestrator). /simplify ran as four parallel review agents (findings applied: per-locale carrier index, iter_segments hoist, slot_type_of promotion, dead guard, TSV-only, wildcard constant; perf 5.35s -> 0.27s on the 50-candidate --all run). code-review high ran twice-scope complete on the branch (findings applied: typo'd-intent refusal, file+flag refusal, TSV edge columns, --out in text mode, help fix, docs heading move, four contract tests). The sixth synthetic-model builder is tracked as JF-972. AC #5 executed: the JF-965 candidate table (/tmp/jf965_candidates.tsv, 17 candidates) corrected that task's description to the real twelve-row delta.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
carriers subcommand landed on main (merge after two review rounds): sample-level carrier lookup with per-(candidate, locale) verdicts ABSENT/PRESENT/SAME_CARRIER, per-locale carrier index (50-candidate --all 5.35s to 0.27s), the placeholder walk hoisted to normalize.iter_segments shared with compile_pattern, slot_type_of promoted to effective_model, TSV candidate files with the snapshot glob wildcard, hard refusals for typo'd intents and no-effect flag combinations, --out honored in both formats. 16 unit/contract tests; docs section with the match-is-not-a-presence-check caveat. AC #5 executed: the JF-965 candidate table (17 rows) corrected that task to its real twelve-row delta (five premises of the JF-963 research disproven by the tool: en by-order, en artist-shuffle, fr start-album all PRESENT).
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
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

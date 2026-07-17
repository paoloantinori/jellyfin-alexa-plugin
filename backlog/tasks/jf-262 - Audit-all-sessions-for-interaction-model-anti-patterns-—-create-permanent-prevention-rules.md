---
id: JF-262
title: >-
  Audit all sessions for interaction model anti-patterns — create permanent
  prevention rules
status: Done
assignee: []
created_date: '2026-06-06 08:00'
updated_date: '2026-06-06 15:37'
labels:
  - documentation
  - nlu
  - interaction-model
  - anti-patterns
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
We keep re-introducing the same classes of NLU/interaction model bugs. Analyze all working sessions (conversation transcripts, git history, and backlog tasks) to identify recurring anti-patterns in interaction model editing, then codify them as permanent rules in CLAUDE.md.

**Known recurring patterns so far:**

1. **Static samples without slots** — Adding concrete utterances like "mostra artisti" without `{browse_category}` teaches NLU to match the intent but NOT fill the slot. This has happened multiple times (JF-260 added "mostra libri/artisti/film/album" as static anchors, which then broke slot resolution).

2. **Static samples competing with slotted variants** — Adding "Play random songs" alongside "Play random {media_type} songs" — the static variant wins NLU match without filling the slot (JF-257).

3. **Missing slot type values** — Test fixtures use album/song/artist names not present in the custom slot type (e.g., "Abbey Road" not in AlbumName, causing empty slot resolution).

4. **Adding vocabulary without checking slot coverage** — New imperative/infinitive verbs added to vocabulary but only tested for routing, not slot filling.

5. **Case sensitivity false duplicates** — "Mostra {browse_category}" vs "mostra {browse_category}" are identical to Alexa NLU but look different in the template.

**Deliverables:**
1. Comprehensive list of anti-patterns found across all sessions
2. Rules added to CLAUDE.md under "Interaction Model Anti-Patterns" section
3. Each rule should include: pattern name, description, example of wrong vs right, and how to detect it

**Sources to analyze:**
- Git log: `git log --oneline --grep="NLU\|interaction\|model\|slot\|intent\|routing" -- .`
- Backlog tasks with NLU/e2e/slot labels
- Conversation transcripts in memory directory
- The YAML template and model JSON diff history
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added "Interaction Model Anti-Patterns — DO NOT REPEAT" section to CLAUDE.md with 8 codified rules.

**Anti-patterns documented:**
1. Static samples without slots (7+ incidents) — MOST COMMON
2. AMAZON.SearchQuery coexistence (9+ incidents)
3. NLU intent competition (9+ incidents)
4. Cross-locale drift (8+ incidents)
5. Custom samples on built-in intents (3 incidents)
6. Vocabulary expansion side effects (YAML template)
7. Slot value guards must use IsNullOrWhiteSpace
8. Missing slot type values for test fixtures

Each rule includes: pattern name, incident count, wrong vs right examples, and detection method (grep pattern or validation script check).

**Sources analyzed:** 50+ git commits, 30+ backlog tasks, existing memory files, interaction model template, and validation scripts.

**Commit:** 0bd8066
<!-- SECTION:FINAL_SUMMARY:END -->

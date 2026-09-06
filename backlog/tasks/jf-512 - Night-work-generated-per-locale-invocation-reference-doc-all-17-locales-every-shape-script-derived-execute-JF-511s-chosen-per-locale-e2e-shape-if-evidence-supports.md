---
id: JF-512
title: >-
  Night work: generated per-locale invocation reference doc (all 17 locales,
  every shape, script-derived) + execute JF-511's chosen per-locale e2e shape if
  evidence supports
status: To Do
assignee: []
created_date: '2026-09-06 20:13'
labels:
  - documentation
  - i18n
  - e2e
  - night-work
dependencies: []
references:
  - JF-511
  - JF-510
  - VOICE_COMMANDS.md
  - 'CLAUDE.md anti-pattern #11'
  - LocaleInvocationNames
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Overnight directive from Paolo (2026-09-06 evening): once the current queue clears (JF-510 e2e refresh in progress), the night's focus is (1) per-locale e2e and (2) a complete invocation reference doc. Related: JF-511 is the design study for per-locale e2e - if its evidence gathering supports it, EXECUTE the chosen shape this night rather than leaving it a study; JF-510's refreshed it-IT fixtures are the pattern source.

PART 2 - THE DOC (new, this task's deliverable): create docs/VOICE_COMMANDS_BY_LOCALE.md (or extend the docs-site structure per the existing conventions) listing EVERY supported invocation shape for ALL 17 locales, sourced from the models themselves (generate, do not hand-write: a script that reads model_*.json and emits the table per locale, intent by intent, mirroring VOICE_COMMANDS.md's per-locale rows but complete and auto-derived so it cannot drift - the VOICE_COMMANDS.md manual mirror went stale twice, JF-459/JF-494). Include: per locale, each intent's sample set with slot placeholders rendered readably, grouped by what the user wants to do (play music / play video / episodes / search / queue control / favorites / radio / books), and a per-locale invocation-name note (it-IT 'mia collezione' vs the others 'jellyfin player' per the LocaleInvocationNames config). The doc is user-facing: plain language, no internal handler/intent names in the body (an appendix may map intent names for maintainers). One-shot forms documented per locale where the infinitive convention exists (JF-493's per-locale analysis already classified which locales have them). Wire it into the docs-site if the existing docs mirrors require it (anti-pattern #11 obligations), and make the generator script part of the repo (scripts/generate_voice_reference.py) so the next model change regenerates it.
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

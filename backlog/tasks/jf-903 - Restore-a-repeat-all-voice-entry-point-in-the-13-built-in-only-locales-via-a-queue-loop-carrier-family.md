---
id: JF-903
title: >-
  Restore a repeat-all voice entry point in the 13 built-in-only locales via
  a queue-loop carrier family
status: To Do
assignee: []
created_date: '2026-10-17 15:30'
updated_date: '2026-10-17 15:30'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-902 -
    Post-JF-901-loop-consistency-move-the-bare-loop-sample-rows-off-LoopAllOnIntent-in-the-4-custom-locales-and-sync-the-loop-semantics-docs-mirrors.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from JF-902 item 1b (re-filed as its own row instead of half-done inside
JF-902; the 13-locale translation surface carries its own review weight).

Since JF-901 the built-in AMAZON.LoopOnIntent applies repeat-ONE, and
LoopAllOnIntent (repeat-ALL, LoopOnIntentHandler) is declared in only 4 locales
(it-IT, de-DE, fr-FR, fr-CA; each now with a queue-scoped carrier family like
"Ripeti la coda" from JF-902). The other 13 locales (ar-SA, en-AU, en-CA,
en-GB, en-IN, en-US, es-ES, es-MX, es-US, hi-IN, ja-JP, nl-NL, pt-BR) declare
only the built-ins, so they have NO voice path to repeat-all at all: bare
"loop on" now repeats the current track. This is the documented accepted trade
of the JF-901 maintainer decision (option a); this task is the optional
restoration.

The fix needs a queue-loop carrier family translated across all 13 locales
(custom intent samples naming the queue, e.g. "repeat the queue", per the
JF-902 it-IT/de/fr precedents), NOT custom samples on the AMAZON.LoopOnIntent
built-in itself (anti-pattern #5). Two shapes to decide between: extend
LoopAllOnIntent's declaration to all 17 locales with per-locale queue-scoped
samples (matches the 4 custom locales; also gives LoopAllOffIntent symmetry
considerations), or a new queue-loop intent. Either way: 13 template edits +
regeneration, NLU fixture rows in 13 locales, VOICE_COMMANDS regeneration,
docs mirrors (queue-radio-*.md gained no queue-loop node in these 13 at
JF-902 precisely because the voice path is absent; adding one back requires
the node + parse_mermaid + data.json sync), and the validator's intent-roster
expectations (InteractionModelTests CustomIntentsWithoutSlots already covers
LoopAllOnIntent if declared). Live locale-routing probes on a representative
subset before deploying to all 13.
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

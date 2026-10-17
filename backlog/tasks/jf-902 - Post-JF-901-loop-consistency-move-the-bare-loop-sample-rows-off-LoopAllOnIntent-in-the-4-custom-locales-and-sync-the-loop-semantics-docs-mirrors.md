---
id: JF-902
title: >-
  Post-JF-901 loop consistency: move the bare loop sample rows off
  LoopAllOnIntent in the 4 custom locales and sync the loop-semantics
  docs/mirrors
status: To Do
assignee: []
created_date: '2026-10-17 13:06'
updated_date: '2026-10-17 13:18'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-901 -
    Bare-loop-and-attiva-il-loop-must-repeat-the-current-track-the-built-in-AMAZON.LoopOnIntent-remaps-to-repeat-one-the-custom-LoopAllOn-keeps-queue-repeat.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-901 /simplify altitude review (2026-10-17): JF-901 moved the built-in AMAZON.LoopOnIntent to repeat-one handler-side, but the model layer and the docs still describe or select the old bare-loop=repeat-all semantics.

Item 1 (the load-bearing divergence): in the four custom-declaring locales (it-IT, de-DE, fr-FR, fr-CA) LoopAllOnIntent still owns the BARE loop-activation sample rows (it-IT: "Attiva loop", "Attiva ripetizione", "attivare loop", the "attiva il loop" article twins per JF-889; de-DE: "Schleife an", "Wiederholung an"; template lines around it-IT.yaml:147), so the same spoken phrase now maps to two intents applying OPPOSITE repeat modes: the custom exact rows (repeat-all) and the built-in (repeat-one, which won live in corr 6883f581). Per the NLU-trainer-nondeterminism memory the trainer pick flips across rebuilds; on a flip the exact phrase the maintainer decision targeted ("attiva il loop") returns to repeat-all and re-triggers the 4e6ce000 bug class. The fix: move the bare loop rows off LoopAllOnIntent (onto the repeat-one side or dropped in favor of the built-in, per the JF-889 exact-row discipline) leaving LoopAllOn genuinely queue-scoped wording (e.g. it-IT "ripeti la coda" family), with NLU fixtures, VOICE_COMMANDS.md regeneration, docs mirrors (parse_mermaid for graphs.json + data.json), and a live locale-routing probe in the same change. All 4 locales; the other 13 declare only the built-in and need no sample change.

Item 1b (JF-901 code-review finding, verified against the models 2026-10-17): LoopOnIntentHandler is the plugin's ONLY writer of RepeatMode.RepeatAll, and LoopAllOnIntent is declared in only those 4 locales while AMAZON.LoopOnIntent is declared in all 17. After JF-901 the 13 built-in-only locales therefore have NO voice path to repeat-all at all (before, bare "loop on" gave queue repeat). The maintainer decision scoped this task's handler-side fix to exactly that trade, and the IntentNames comment documents the gap; restoring a queue-repeat entry point in those 13 locales needs a queue-loop carrier family (custom intent or built-in samples per the anti-pattern #5 constraints) and belongs with item 1's model-side pass.

Item 2 (stale docs, independent of item 1): docs/queue-radio-*.md (17 files) label the node "AMAZON.LoopOnIntent / Loop entire queue" (+ localized equivalents: "Hele wachtrij herhalen", "Repetir toda a fila", "Répéter toute la file", Hindi, Arabic, Japanese), docs/playback-lifecycle-*.md use the node id LoopAll for the built-in, and docs/VOICE_COMMANDS_BY_LOCALE.md row "Playback control | Repeat all on | AMAZON.LoopOnIntent" (EMITTED file: fix the generator's label mapping, never hand-edit, then regenerate). All now contradict JF-901 (built-in = repeat current track). After any docs md change re-run python3 docs-site/parse_mermaid.py from the repo root and copy the output to both graphs.json mirrors, and hand-sync docs-site/data.json embedded mermaid strings from the md sources.

Scope note: JF-901 deliberately excluded the model surface (maintainer decision option a, handler-side only); this task is the deferred consistency pass the JF-901 task file named as a possible follow-up.
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

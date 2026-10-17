---
id: JF-902
title: >-
  Post-JF-901 loop consistency: move the bare loop sample rows off
  LoopAllOnIntent in the 4 custom locales and sync the loop-semantics
  docs/mirrors
status: Done
assignee: []
created_date: '2026-10-17 13:06'
updated_date: '2026-10-17 15:22'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf902 (64157daf + the orchestrator's JF-904 renumber fixup), merged to main 4b24334c, pushed, DEPLOYED end to end: DLL to minix (md5-verified), all 4 locale model rebuilds SUCCEEDED via the plugin rebuild endpoint (never raw SMAPI PUT), live routing probes exactly as designed (it 'attiva il loop'/'Schleife an'/'Active la boucle' -> LoopSongOnIntent; 'ripeti la coda'/'Wiederhole die Warteschlange'/'Répète la file d'attente' -> LoopAllOnIntent), and the live NLU suite green 43/43 on the loop-scoped selection (the red-until-deploy rows included). ITEM 1: the bare loop rows MOVED onto LoopSongOnIntent (not dropped: the JF-887/JF-889 steal evidence plus today's new-phrase steals proved bare rows must exist on the repeat-one side), LoopAllOn now carries only queue-named carriers localized per the template conventions, de/fr infinitive twins added (code-review), fixtures x4 with A|B alternation pins, wrapper battery rows flipped. ITEM 2: generator labels fixed and both VOICE_COMMANDS docs regenerated, 34 docs md synced (queue-radio + playback-lifecycle x17 each), graphs.json mirrors + data.json synced (16 pre-existing session-management entries swept with the wholesale rule). ITEM 1B: re-filed as its own row, RENUMBERED JF-904 by the orchestrator (its worker-file id collided with the truncated-tail JF-903 filed on main minutes earlier); the 13-locale translation surface untouched. Gates (in 64157daf): 0-warning build both TFMs Debug+Release -warnaserror, 6041/6041 per TFM, validate_interaction_models PASS at the 275 baseline, validate_locales PASS, voice-reference --check OK, parse_mermaid clean, NLU dry-run 43, audit_backlog_orphans --check PASS, /simplify all applied, code-review high 6/6 applied, live probes documented the trainer flip. Residuals: the es loop edge accents + the it lifecycle LoopSong label are pre-existing doc nits noted by the worker. gate-exempt: gates ran in the dispatched worker's transcript; commit 64157daf carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->

---
id: JF-494
title: >-
  VOICE_COMMANDS.md lacks PlayNextEpisode rows in all 17 locales (JF-324 mirror
  gap; JF-493 added samples to the intent without adding rows)
status: In Progress
assignee: []
created_date: '2026-09-05 12:40'
updated_date: '2026-09-05 15:16'
labels:
  - documentation
  - tv
  - nlu
dependencies: []
references:
  - JF-324
  - JF-493
  - VOICE_COMMANDS.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-324 added PlayNextEpisodeIntent to all 17 locale models but never added the intent's rows to VOICE_COMMANDS.md (the hand-maintained per-locale utterance table). JF-493 (2026-09-05) added infinitive one-shot samples to the intent in 9 locales and updated only the Play Episode rows of the mirror, leaving the PlayNextEpisode gap unchanged (pre-existing, out of that diff's scope). The table therefore documents no way to ask for the next/last episode in any locale even though every model carries the intent. Add a "Play Next Episode" row per locale section (17 rows) mirroring each model's current PlayNextEpisodeIntent samples, including the JF-493 infinitive twins (it-IT "{infinitive} il prossimo/l'ultimo episodio di {series_name}", "{infinitive} la serie {series_name}"; en "to play the next episode..."; de "Zu spielen die nächste folge..."; fr "De jouer le prochain épisode..."). Pure documentation change, no code or model edits.
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

<!-- NOTES:BEGIN -->
2026-09-05 (JF-494 implementation): added a `Play Next Episode` row to each of the 17 locale
tables in VOICE_COMMANDS.md, inserted between `Play Next` and `Play Playlist` (alphabetical row
order used by the tables). Sample lists copied verbatim from each locale's
`Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_<locale>.json`
`PlayNextEpisodeIntent.samples`, preserving JSON order, backtick wrapping, and the ` · `
separator. Scripted cross-check: all 17 locales EXACT MATCH (order and content), 17 insertions,
0 deletions in the file diff. Includes the JF-493 infinitive twins (it-IT `Di riprodurre ...`,
en `to play ...`, de `Zu spielen ...`, fr `De jouer ...`).

Doc-only change (VOICE_COMMANDS.md plus this note): no code, model, or fixture edits, so DoD
items 1-8 are unaffected by this diff.

### Mirrors now stale (for the orchestrator to file; NOT touched in this task, out of scope)

The CLAUDE.md anti-pattern #11 mirror list requires updates when samples change. Conservative
reading (any intent, added flow): these mirrors lack the PlayNextEpisode flow entirely and would
need it:

1. `docs/playback-lifecycle-<locale>.md` (all 17 files): no `PlayNextEpisode` node and no
   `Idle -->|"<sample>"| PlayNextEpisode` edge for any of the intent's samples in any locale.
   Note the staleness is wider than this intent: `PlayEpisodeIntent` (the JF-324 sibling) is
   ALSO absent from every playback-lifecycle diagram, so the episode-playback flows are missing
   wholesale, not only the JF-493/JF-494 additions.
2. `docs/graphs.json` and `docs-site/graphs.json` (identical mirrors of the md diagrams): zero
   episode-playback nodes; a `python3 docs-site/parse_mermaid.py` regen is a no-op until the
   mds gain the edges.
3. `docs-site/data.json` (embedded mermaid strings): same absence; must be updated from the md
   sources once they change.

Not stale from this task, for the record:

- `tests/integration/fixtures/<locale>.yaml`: no sample was removed or changed, so no NLU
  expectation can have gone stale. Pre-existing coverage: en-US.yaml and it-IT.yaml already
  assert PlayNextEpisodeIntent (5 cases each, including the JF-493 infinitive twins); the other
  15 locale NLU fixtures have no PlayNextEpisode cases at all (JF-324/JF-493 coverage gap, only
  relevant if per-locale NLU coverage for this intent is wanted).
<!-- NOTES:END -->

---
id: JF-606
title: >-
  it-IT missing the bare noun-first create sample: add 'Nuova playlist
  {playlist}' (en/de/es/fr/pt/nl all got one)
status: Done
assignee: []
created_date: '2026-09-20 19:18'
updated_date: '2026-09-20 21:15'
labels: []
dependencies: []
references:
  - commit 560b484c
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of 560b484c (JF-600): the bare noun-first create sample was added to en ('new playlist {playlist}'), de ('Neue Playlist {playlist}'), es ('Nueva lista {playlist}'), fr ('Nouvelle liste de lecture {playlist}'), pt ('Nova playlist {playlist}') and nl ('Nieuwe afspeellijst {playlist}'), but it-IT (the live household locale) only got the three Crea-family forms; its only noun-first form remains the carrier-full 'Nuova playlist chiamata {playlist}' (templates/it-IT.yaml ~line 965). An it-IT user saying 'nuova playlist prova echo' has no matching CreatePlaylistIntent sample and the utterance falls toward AddSongToPlaylistIntent's greedy free-text slot, the exact JF-600 misroute class in its primary locale. ar-SA is the only other locale without a noun-first form (verify whether that is deliberate Arabic phrasing or the same gap while there). Edit the template, regenerate the model, and update every mirror the repo's anti-pattern #11 rule names.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 templates/it-IT.yaml carries 'Nuova playlist {playlist}' and the model regenerates byte-exact
- [ ] #2 All emitted mirrors update in the same change: VOICE_COMMANDS.md AND docs/VOICE_COMMANDS_BY_LOCALE.md (see JF-603 for the mirror that went stale), docs graphs if the CreatePlaylist edge labels change
- [ ] #3 An NLU fixture row covers 'nuova playlist <name>' routing to CreatePlaylistIntent
- [ ] #4 No static slotless sample is introduced (anti-pattern #1) and no SearchQuery coexistence violation (anti-pattern #2)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented 2026-09-20 (uncommitted, under gates): it-IT gained "Nuova playlist {playlist}" (noun-first, matching en/de/es/fr/pt/nl); ar-SA gained "قائمة تشغيل جديدة {playlist}" (the noun-first gap the task flagged, closed by adding the form rather than only a note); both models regenerated, NLU fixture row "nuova playlist prova echo" -> CreatePlaylistIntent added (AC#3).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed 2026-09-20 (commit 72b1989a): it-IT gained "Nuova playlist {playlist}" and ar-SA "قائمة تشغيل جديدة {playlist}"; models regenerated, voice-reference mirrors fresh, NLU fixture row "nuova playlist prova echo" -> CreatePlaylistIntent added and live-verified via profile-nlu on the rebuilt model.
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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

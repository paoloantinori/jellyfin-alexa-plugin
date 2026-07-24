---
id: JF-355
title: >-
  Mood feature: default to Spotify's mood list + expose the mood list in user
  settings (follow-up to JF-354)
status: To Do
assignee: []
created_date: '2026-07-20 07:43'
updated_date: '2026-07-20 09:04'
labels:
  - mood
  - config
  - per-user
  - spotify
  - follow-up
  - i18n
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up to JF-354 (the custom Mood slot type). Two refinements the maintainer wants:

1. **Default the Mood slot-type values to Spotify's mood list** (the golden standard). Research Spotify's supported moods (Happy, Sad, Chill/Relax, Workout/Energy, Focus, Party, Sleep, Romance, etc.) + map each to the handler's genre arrays (MoodGenreMap). The current Mood values (JF-354) are the LocalizedMoodMap keys; replace/augment with the Spotify-standard list so users get a familiar vocabulary. Localize for all 17 locales.

2. **Expose the mood list in user settings** (configurable per-user): a user can add/remove/customize moods. This likely needs the Mood slot type to be populated per-user (dynamic-entity updates via SMAPI at session start, OR a global default + per-user overrides). Assess the mechanism (dynamic entities vs per-user model slice).

Do this AFTER JF-354's pioneer (it-IT custom Mood type) is verified + rolled to 17 locales. Not blocking the pioneer.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[Part 1 DONE + deployed 2026-07-20] Spotify alignment: added 'sleep' to MoodGenreMap (ambient/new age/chillout/acoustic) + localized sleep words (dormire, per dormire, schlafen, einschlafen, sueno, sommeil) and it-IT unaccented energetic (energica/energico) to LocalizedMoodMap. it-IT Mood slot: +dormire (syn: per dormire), +energica (syn: energico). Spotify's canonical list (Chill/Happy/Sad/Focus/Workout/Party/Sleep/Romance/Energy/Dinner) now fully covered.

[Part 1 review] /code-review high (opus, scoped): 0 defects. Surfaced + fixed: (a) IsNullOrEmpty->IsNullOrWhiteSpace guard at line 332 (anti-pattern #7); (b) corrected misleading 'dormire covers es/fr/pt' comment (it-IT infinitive only; es/fr/pt say 'dormir', deferred to JF-356).

[Part 1 verified live] profile-nlu: musica per dormire -> PlayMoodMusic (mood=per dormire -> sleep); musica energica -> PlayMoodMusic (mood=energica); musica di norah jones -> PlayArtistSongs (JF-354 regression-safe); musica rilassante -> PlayMoodMusic (mood=rilassante). DLL hot-swapped (size match 2093056), config survived (1 user).

[Part 1 DoD] build 0 warn, test 2542/2542, validate_interaction_models+locales+versions PASS, /code-review high PASS. /simplify: changeset is pure additive data (no logic) - 4 angles have no targets, manual pass clean. NLU fixture +2 rows (dormire/energica).

[Part 2 ASSESSMENT] Per-user model slice = IMPOSSIBLE (SMAPI models are 1-per-skill-per-stage; IInteractionModelRedeployer rebuilds all 17 for the whole skill, not per-user). Dynamic entities = only viable per-user lever: DynamicEntitiesInterceptor already fires on new sessions (DynamicEntitiesInterceptor.cs:60) pushing catalog slots via Dialog.UpdateDynamicEntities, BUT dynamic entities are a response directive => turn-2+ only. Moods are one-shot ('musica per dormire') so turn-1 uses the static model; dynamic entities add little. Recommendation given low usage: DEFER the full customizer. Lightest viable future path if wanted: admin-level config field (PluginConfiguration.MoodGenreOverrides dict) merged into MoodGenreMap at resolve time + IInteractionModelRedeployer trigger on save (mirrors the invocation-name redeploy path at ConfigurationController.cs:247). Doable in JF-356's wake.
<!-- SECTION:NOTES:END -->

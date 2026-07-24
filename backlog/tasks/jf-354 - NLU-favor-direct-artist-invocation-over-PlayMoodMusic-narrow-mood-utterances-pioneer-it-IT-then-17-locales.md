---
id: JF-354
title: >-
  NLU: favor direct artist invocation over PlayMoodMusic (narrow mood
  utterances) -- pioneer it-IT, then 17 locales
status: Done
assignee:
  - claude
created_date: '2026-07-20 06:02'
updated_date: '2026-07-20 08:08'
labels:
  - nlu
  - interaction-model
  - mood
  - artist-search
  - i18n
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
"musica di norah jones" misroutes to PlayMoodMusicIntent (mood="di norah jones") instead of PlayArtistSongsIntent -- the greedy AMAZON.SearchQuery mood slot + "musica {mood}" carrier captures "music by X" (CLAUDE.md anti-pattern #3). It works (handler-side artist fallback plays Norah Jones) but wastes a ~1s genre query + adds latency, and the user rarely uses mood. Fix: narrow PlayMoodMusic's utterances so they don't win the prefix competition against direct artist invocation -- favor PlayArtistSongs. Options: (a) more precise mood utterances (mood-specific carriers/keywords that don't collide with "musica di {artist}"); (b) mood only via a clarifying question. Pioneer for it-IT (edit the YAML template + regenerate), verify via profile-nlu + on-device that "musica di norah jones" -> PlayArtistSongs AND mood utterances still route to PlayMoodMusic. Then verify/roll out to the other 16 locales.
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

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-20 08:08
---
it-IT PIONEER DELIVERED + VERIFIED (commits 54bd2fa + c475466). Two-part fix: (1) PlayArtistSongs gained the bare '{media_noun} {artist_article} {musician}' template (artist_article includes 'di') so 'musica di norah jones' routes directly to PlayArtistSongs (previously only verb-prefixed templates existed -> the bare form fell to PlayMoodMusic). (2) PlayMoodMusic's mood slot changed from the greedy AMAZON.SearchQuery to a custom Mood slot type (populated with the it-IT mood vocabulary from LocalizedMoodMap) + 'musica {mood}' + 'musica per {mood}' carriers. The custom type captures only mood values -> 'musica di norah jones' (not a mood) no longer matches -> PlayArtistSongs; 'musica rilassante' -> mood filled (no empty-slot trap). Handler: added infinitive mood forms (allenarmi/concentrarmi/rilassarmi) to LocalizedMoodMap so the Mood slot's infinitive synonyms resolve. VERIFIED via profile-nlu on the live+built it-IT model: 'musica di norah jones' -> PlayArtistSongsIntent (musician=norah jones) [was PlayMoodMusic]; 'musica rilassante' -> PlayMoodMusicIntent (mood=rilassante); 'musica per allenarmi' -> PlayMoodMusicIntent (mood=allenarmi -> workout). Deployed (model + DLL). FULL SUITE 2542/2542. 16-locale rollout = JF-356; Spotify mood-list + user-settings exposure = JF-355.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
it-IT pioneer for favoring direct artist invocation over PlayMoodMusic. Custom Mood slot type (replaces the greedy AMAZON.SearchQuery) restricts the mood slot to the mood vocabulary, so 'musica di norah jones' (not a mood value) routes to PlayArtistSongs (via the new bare artist template) instead of PlayMoodMusic. Adjective moods ('musica rilassante') still fill the slot (no empty-slot trap). Infinitive mood forms (allenarmi/concentrarmi/rilassarmi) added to the handler's LocalizedMoodMap for resolution. Verified via profile-nlu on the live it-IT model; deployed (model + DLL). 16-locale rollout tracked as JF-356; Spotify mood-list + user-settings as JF-355.
<!-- SECTION:FINAL_SUMMARY:END -->

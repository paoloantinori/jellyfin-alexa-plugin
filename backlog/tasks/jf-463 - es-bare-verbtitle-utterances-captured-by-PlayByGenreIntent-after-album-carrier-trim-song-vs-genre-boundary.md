---
id: JF-463
title: >-
  es bare verb+title utterances captured by PlayByGenreIntent after
  album-carrier trim (song-vs-genre boundary)
status: To Do
assignee: []
created_date: '2026-09-03 06:23'
labels: []
dependencies: []
references:
  - JF-459 live matrix evidence (this task description)
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_es-ES.json
    PlayByGenreIntent
  - 'CLAUDE.md anti-pattern #11 (carrier classes)'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-459 live verification matrix (2026-09-03, profile-nlu es-ES): after the album-carrier trim, bare verb+title utterances in Spanish do NOT land on PlaySongIntent uniformly. Probe results (skill 33dfacd5, development stage):
- "Reproduce abbey road" -> PlayByGenreIntent (genre slot captures "abbey road")
- "Pon abbey road" -> PlayByGenreIntent
- "Escucha abbey road" -> PlaySongIntent
- "Toca abbey road" -> PlaySongIntent

Cause: es-ES/es-MX/es-US PlayByGenreIntent carries TWO bare carriers ("Reproduce {genre}", "Pon {genre}") that outrank PlaySongIntent's "Reproduce {song}" / "Pon {song}" in the es NLU. The bare {genre} carrier itself is deliberate repo-wide design (all 17 locales have one: "Play {genre}" en, "Spiele {genre}" de, etc., because "play jazz" must route to genre), so the fix is NOT a blanket genre-carrier trim. The specific problem is the OVERLAP in Spanish: the same bare verbs as the song intent, plural carriers (2 where other locales have 1), and no genre-noun requirement.

Investigate (probe-first per repo rules):
1. Whether the es PlayByGenreIntent can drop to ONE bare carrier (keep the most genre-typical verb) or require a noun on one of the two ("Pon musica {genre}" exists already as "Reproduce música {genre}"), so "Reproduce X"/"Pon X" fall to PlaySongIntent and the JF-345/JF-295 cascades recover genre-vs-album-vs-song server-side.
2. The same probe in es-MX and es-US (identical sample sets) to confirm the behavior reproduces.
3. What the PlayByGenre HANDLER does with an unresolved genre value ("abbey road" is no genre): today it presumably speaks genre-not-found. A handler-side fallback (unresolved genre value that fuzzy-matches no genre could fall to PlaySong/cascade) is an alternative or complementary fix; check TryEntityFallbackAsync applicability.
4. en-US control: "play abbey road" -> PlaySongIntent (verified), so English single-carrier shape does not exhibit the steal.

Acceptance criteria:
- es-ES "Reproduce abbey road" and "Pon abbey road" route to PlaySongIntent (probe-verified 3/3 deterministic each).
- "Reproduce jazz" and "Pon jazz" still route to PlayByGenreIntent.
- No regression in the other 16 locales' genre routing (probe "play jazz"/"Spiele jazz" before and after).
- NLU fixtures updated if samples change.
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

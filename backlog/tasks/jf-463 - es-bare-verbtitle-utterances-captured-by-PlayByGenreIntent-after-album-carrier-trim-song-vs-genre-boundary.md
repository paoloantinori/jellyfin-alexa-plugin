---
id: JF-463
title: >-
  es bare verb+title utterances captured by PlayByGenreIntent after
  album-carrier trim (song-vs-genre boundary)
status: Done
assignee: []
created_date: '2026-09-03 06:23'
updated_date: '2026-09-03 08:20'
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

Cause: es-ES/es-MX/es-US PlayByGenreIntent carries TWO bare carriers ("Reproduce {genre}", "Pon {genre}") that outrank PlaySongIntent's "Reproduce {song}" / "Pon {song}" in the es NLU. The bare {genre} carrier is deliberate repo-wide design (all 17 locales have one; "play jazz" must work), and the genre slot is the built-in AMAZON.Genre in ALL locales (verified 2026-09-03), so the model-layer fix would be the JF-354 custom-type conversion at 17-locale scale.

RESOLUTION DEPTH (amended 2026-09-03 after the slot-type discovery; the original ACs presumed a model-layer fix): the proportional fix is HANDLER-SIDE, mirroring the PlayMoodMusic precedent: when PlayByGenreIntentHandler receives a genre value that resolves to NO known genre (not in its genre map, no admin override, no fuzzy match), it should fall back via the existing BaseHandler.TryEntityFallbackAsync (artist search on the tokenized value, word-count guard, threshold gate) exactly like PlayMoodMusic does for mood misses, then its own song/album not-found path if that misses too. This recovers the user's intent server-side for every locale, not just es, without touching 17 models. The model-layer conversion (custom Genre type with per-locale vocabulary, JF-354 mirror) stays a possible future task if handler-side proves insufficient.

Investigation still required before implementing:
1. Read PlayByGenreIntentHandler's current genre-resolution path: what happens today with genre="abbey road" (genre-not-found speech? fuzzy attempt?). Confirm TryEntityFallbackAsync is NOT already wired there.
2. Reproduce the probes in es-MX and es-US (identical sample sets; confirm the same steal).
3. Check the PlayMoodMusic wiring of TryEntityFallbackAsync for the exact call shape (tokenize, threshold, word guard, announcement, null return on miss).
4. Verify a REAL genre still resolves normally (no behavior change for "Reproduce jazz": the fallback must fire ONLY on confirmed genre-resolution failure).

Acceptance criteria (amended):
- Handler: a genre value resolving to no genre triggers TryEntityFallbackAsync; a confirmed artist match plays with the FoundArtistInstead announcement (respecting AnnounceCrossMediaSubstitution); no match falls through to the existing not-found.
- Unit tests: unresolved-genre + artist-exists plays the artist; unresolved-genre + nothing-found keeps the genre not-found; resolved genre (e.g. "jazz") never consults the fallback (no artist query issued).
- Probe evidence recorded: es-ES/MX/US "Reproduce abbey road"/"Pon abbey road" steal reproduced pre-fix (the handler now recovers it; profile-nlu still shows PlayByGenreIntent routing, which is expected and fine: the fix is server-side).
- "Reproduce jazz" behavior unchanged end-to-end (simulator or unit test).
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

<!-- SECTION:NOTES:BEGIN -->
FACT CORRECTION (worker ground-truth scan, 2026-09-03): the genre slot is AMAZON.Genre in 16 locales but AMAZON.SearchQuery in model_it-IT.json (PlayByGenreIntent). The 'AMAZON.Genre in ALL locales' claim earlier in this description is wrong for it-IT; the handler-side fix is locale-agnostic either way.

Live probe matrix recorded pre-fix: es-MX 'Reproduce abbey road' steals to PlayByGenreIntent (matches es-ES); es-MX 'Pon abbey road' and es-US 'Reproduce abbey road' went to PlaySongIntent, es-US 'Pon abbey road' to PlayArtistSongsIntent (NLU per-model probabilistic: the steal is intermittent, which is exactly why the handler-side recovery covers all locales); es-ES 'Reproduce jazz' stays PlayByGenreIntent (genre path intact).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Handler-side fix implemented, deployed, and live-verified (commit 9bc80c78). PlayByGenreIntentHandler now mirrors the PlayMoodMusic recovery: on a confirmed genre miss (items.Count == 0) it calls the shared BaseHandler.TryEntityFallbackAsync (identical ctor shape, identical IndexWarmingGate entry placement before the progressive response, identical 12-arg call with the suggestion band off); a confident artist match plays with the FoundArtistInstead announcement, a miss falls through to the unchanged NotFoundGenre.

Live verification on minix (simulator, it-IT, active DLL verified by UTF-16 marker 'PlayByGenre artist fallback'):
- genre="pink floyd" -> AudioPlayer + "Ho trovato l'artista Pink Floyd. Ecco la musica di Pink Floyd."; log shows the fallback label with score=100 threshold=85.
- genre="xyzzyfoo" -> NotFoundGenre tell, no playback (fallback miss path).
- genre="Alternative" (real library genre) -> normal genre path, playback, no announcement (fallback never consulted).

Hardening folded in from the /simplify and code-review passes: slot guard IsNullOrEmpty -> IsNullOrWhiteSpace (anti-pattern #7), the PlayByGenre entry-gate case added to SkillWarmingUpTests (the gate line was previously untested), the Layer-1 enumerations in CLAUDE.md and SkillWarmingUpTests updated, GetPlayDirective/GetSpeechTextOrNull hoisted to TestHelpers at their second copy, dead query-recording parameter dropped from the test helper. Suite 3061/3061 (5 new tests), Release 0 warnings, validators PASS, no interaction-model changes (probe evidence confirms profile-nlu still routes the stolen utterances to PlayByGenreIntent, which is expected: the recovery is server-side).

Deploy note: the systemctl restart reported a timeout while the server completed a ~30s startup (index loads); server, plugin, and config all verified healthy after.

Known limitations recorded: the JF-363 suggestion band [60,85) is off for this path (inherited from the PlayMoodMusic mirror); the MusicEnabled leak in the shared gate is filed as JF-464; test-fixture duplication and the eight-copy warming-gate preamble are filed as the consolidation task created at closure.
<!-- SECTION:FINAL_SUMMARY:END -->

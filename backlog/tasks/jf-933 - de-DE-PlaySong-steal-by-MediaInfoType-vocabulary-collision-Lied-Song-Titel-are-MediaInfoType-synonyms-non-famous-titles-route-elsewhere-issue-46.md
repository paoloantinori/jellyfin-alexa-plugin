---
id: JF-933
title: >-
  de-DE PlaySong steal by MediaInfoType vocabulary collision: Lied/Song/Titel
  are MediaInfoType synonyms, non-famous titles route elsewhere (issue #46)
status: In Progress
assignee: []
created_date: '2026-11-21 05:17'
updated_date: '2026-11-21 18:43'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/46'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Issue #46 (Nschmiederer, 2026-11-20, gold-quality report with Utterance Profiler table): de-DE "Spiele das Lied {song}" routes to MediaInfoIntent or PlayRadioIntent instead of PlaySongIntent when the song title is NOT known to Amazon (children's audio plays); famous titles work, so the committed fixtures (bohemian rapsody/hotel california) are blind to the steal. Verified root cause this session: the generated de-DE model's MediaInfoType carries value "Titel" with synonyms ["Lied", "Song", "Name"] (model_de-DE.json types dump), so PlaySong's carrier nouns are simultaneously vocabulary of the competing MediaInfoType; with the free-text AMAZON.MusicRecording song slot having no Amazon-side anchor for unknown titles, intent selection slides to the static-token match (the JF-642/JF-684 selection-steal family, this time from a type-vocabulary collision rather than a greedy slot). Also verified: de-DE PlaySongIntent has no "den Song {song}" sample (das Lied / den Titel / das Stück only), which explains "spiele den song X" landing on PlayRadioIntent with an unfilled station slot (on-device it asks Welchen Radiosender). Fix directions to weigh: narrow the MediaInfoType synonyms (drop Lied/Song at least), add the den Song carrier, keep the catalog-backed-song-slot idea as a separate larger option (per the JF-642 lesson, selection steals are fixed vocabulary-side first). The reporter offered to run further Utterance Profiler tests: use him for verification passes. Cross-locale audit mandatory: if en-family MediaInfoType carries Title/Song synonyms, the steal exists there too.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Root cause confirmed against the de-DE template/model: the PlaySong carrier nouns colliding with MediaInfoType vocabulary identified precisely, and the collision's presence audited in ALL 17 locale templates (same MediaInfoType family likely carries it in en and others)
- [ ] #2 The chosen fix lands in the YAML templates of every affected locale with models regenerated; never hand-edited JSON
- [ ] #3 The profile-nlu battery (locale-routing-probe) shows 'Spiele das Lied/den Titel/den Song {song}' selecting PlaySongIntent for a NON-FAMOUS in-library title, before and after the fix
- [ ] #4 NLU fixtures gain a non-famous-title row class so this steal class is pinned (today's fixtures only use famous titles like bohemian rhapsody and cannot catch it)
- [ ] #5 The 'den Song' carrier gap in de-DE PlaySongIntent samples is either fixed or explicitly declined with the NLU-competition rationale recorded
- [ ] #6 All stale-mirror rules honored when samples change: VOICE_COMMANDS regen, docs graph labels, fixtures
- [ ] #7 Full suite green both TFMs; validators pass
<!-- AC:END -->

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

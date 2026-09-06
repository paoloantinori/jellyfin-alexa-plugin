---
id: JF-501
title: >-
  Video-launch announcement cut mid-sentence on fast HLS starts: speak via
  progressive response, final response directive-only
status: To Do
assignee: []
created_date: '2026-09-06 08:40'
labels:
  - ux
  - video
  - echoshow
dependencies: []
references:
  - JF-498
  - device test 2026-09-06
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From JF-498's device verification (2026-09-06): the spoken announcement ('Riproduco l'ultimo episodio: X') is cut mid-sentence when the video launches on the Echo Show. With playback WORKING, the HLS playlist is ready in ~0.6s and the VideoApp player takes the audio channel before the TTS finishes; movie launches (static file, longer buffering) let the announcement complete, so the cut is specific to the fast HLS route (and any future fast-start path). Fix shape: move the announcement to a progressive response (SendProgressiveResponse, the machinery already exists and is used for the 'Ricerca dei tuoi contenuti in corso...' pre-announcement) spoken DURING resolution, and return the final response with the VideoApp.Launch directive only (no OutputSpeech); the TTS then completes before the player opens. Scope: the video launch sites that both announce and launch (PlayNextUpEpisodeAsync first, then the movie/episode launch sites for consistency; check whether movies want the same treatment for uniform behavior). Purely cosmetic UX polish; no functional bug.
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

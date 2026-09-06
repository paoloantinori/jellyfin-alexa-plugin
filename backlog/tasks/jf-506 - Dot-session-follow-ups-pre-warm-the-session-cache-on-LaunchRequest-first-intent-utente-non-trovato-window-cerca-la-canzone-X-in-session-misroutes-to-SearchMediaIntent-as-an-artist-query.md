---
id: JF-506
title: >-
  Dot session follow-ups: pre-warm the session cache on LaunchRequest
  (first-intent 'utente non trovato' window) + 'cerca la canzone X' in-session
  misroutes to SearchMediaIntent as an artist query
status: To Do
assignee: []
created_date: '2026-09-06 15:14'
labels:
  - ux
  - nlu
  - session
dependencies: []
references:
  - corr=ce5cb86a
  - corr=3240220d
  - JF-477
  - FindSongIntent
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two smaller findings from the 2026-09-06 Dot session. (A) Session pre-warm on LaunchRequest: a brand-new device's FIRST intent hits the JF-477 2s fast-fail budget and answers 'Utente non trovato. Per favore ricollega il tuo account' (17:08:45 corr=ce5cb86a), self-healing ~6s later when the warm-fill lands (17:08:51); his in-window retries also failed. Improvement: on LaunchRequest, fire the session lookup fire-and-forget so the cache is warm before the first real intent (the 'apri mia collezione' launch almost always precedes the command). (B) 'cerca la canzone screenwriter's blues' in a freshly-reopened session (after the resume-offer NoIntent cleared FindSongSessionData) routed to SearchMediaIntent (17:11:30 corr=3240220d), which ran an ARTIST search on the whole song title and not-founded ('Spiacente non ho trovato il contenuto'); FindSongIntent never saw it. Investigate the in-session intent competition for the 'cerca la canzone {titleKeywords}' carrier (samples? the FindSong controller force-route only applies with FindSongSessionData present) and fix the routing or add disambiguating samples so the song-search shape wins.
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

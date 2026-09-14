---
id: JF-559
title: >-
  Extend amazon:musicArtist SSML role to remaining artist announcements (after
  live validation of the FoundArtistInstead pilot)
status: To Do
assignee: []
created_date: '2026-09-14 14:50'
labels:
  - enhancement
  - ssml
  - phonetic
  - ux
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The <w role='amazon:musicArtist'> SSML wrapper (stolen from a first-party TTS capture, 2026-09-14) is piloted ONLY on the FoundArtistInstead announcement (BaseHandler.BuildArtistAnnouncementSsml). CONTINGENT on live validation: the role is not in the public custom-skill SSML reference, so first confirm on a real channel (console TTS and/or device) that the announcement is spoken and the pronunciation improves. If accepted, extend the wrapper to the remaining artist-speaking announcements (candidates from it-IT keys: NoSongsForArtist, AlbumsByArtistList/Partial, TracksByArtistList/Partial, ArtistInfoBio* family, DisambiguateNext where the arg is an artist, plus the SSML announce variants of artist plays); only SSML-path keys qualify, plain-text keys stay plain. Extension must reuse BuildArtistAnnouncementSsml-style wrapping, never hand-inline the tag.
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

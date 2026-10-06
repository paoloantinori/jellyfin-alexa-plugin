---
id: JF-805
title: >-
  JF-805 - the YesIntent ALBUM confirm leg launches albumItems[0] with no resume scan
  and no continuation: the album twin of the JF-795 confirm==ask unification
status: To Do
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - playback
dependencies:
  - JF-796
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/AlbumPlayService.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-796 gate-marker (2026-10-07), finding 3, same-turn per the
review-recommendation rule.

The YesIntent album confirm leg still always launches `albumItems[0]` with no
FindResumeTrackIndex scan and no QueueContinuation mint. The asymmetry class is
pre-existing (in-page progress already diverged), but two things widened it: JF-795
made confirm == ask STRUCTURAL for books via the shared AudiobookPlayResolver while
the album confirm stayed hand-rolled, and JF-796's deep resume makes the divergence
observable in a shape it previously was not (UserData in-progress on track 22 of 26:
the direct ask now launches track 22; a disambiguation yes launches track 1 with the
whole-album unpaged queue and no continuation).

FIX SHAPE (the JF-795 pattern evaluated for albums): route the album confirm through
BuildAlbumPlayResponseAsync the way the book confirm routes through
PlayBookAsync (the PodcastEpisodeResolver/AudiobookPlayResolver precedent chain),
so the confirm inherits the initial page, the deep resume, the continuation mint, and
the tracker override from the ONE composition. Evaluate whether the album composition
extracts as cleanly as the book head did; the four documented divergence axes of
JF-803 (the tracker veto, resumePosition tiers, the AlbumIds arm, the absolute
prefix) live INSIDE the album composition and ride along, they do not block the
routing.
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

---
id: JF-812
title: >-
  JF-812 - the JF-581 position-store valve on the book deep-resume gate is
  device-global: stamp book-shaped entries at write time so the discriminator
  survives household playback history
status: To Do
assignee: []
created_date: '2026-10-07 09:10'
updated_date: '2026-10-10 12:58'
labels:
  - tech-debt
  - audiobooks
  - performance
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookPlayResolver.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackStoppedEventHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-797 code-review round (2026-10-07, same-turn per the review-recommendation rule), finding F3.

The JF-797 fresh-ask discriminator gates the book head's deep-resume fetch on two triggers: the IsPlayed/IsResumable probe pair (bounded, one row each) and a JF-581 safety valve, DeviceQueueManager.HasAnyStoredPosition, which releases the fetch whenever the device's ItemPositionState holds ANY positive entry. The review established the production consequence: ItemPositionState is written by EVERY qualifying PlaybackStopped (plain songs included, ticks > 0) and persists across restarts, so after a household plays anything through the skill the valve is permanently true and every first-ever ask of a multi-page book pays the pre-JF-797 unconditional unpaged fetch plus the per-chapter GetUserData re-scan. The probes then never execute except on virgin/reset devices. The ALBUM twin is unaffected (no queue tier on that path; it discriminates unconditionally), which is why this is low priority: the row-volume concern the JF-796 addendum named lives on albums, and book chapter lists are bounded.

The JF-581 shape the valve guards is real and must stay covered: a server-side UserData write loss with the position surviving only in the plugin store would be invisible to the UserData probes, and dropping the valve would regress those resumes to chapter 1.

Fix shape (the review's suggestion, evaluated): record the item KIND beside the position at write time, so the valve can key on book-shaped entries only. The single write site is PlaybackStoppedEventHandler's RecordStoppedPositionAndTrim call (it does not hold the BaseItem today; session.FullNowPlayingItem or a GetItemById resolves it, a bounded background-path lookup outside the Alexa window), and DeviceQueue persists as JSON (JsonSerializer with JsonOptions), so an added field deserializes as empty for existing stores (backward compatible). Consider also clearing or aging entries so finished items stop counting.

Verification: a pin that a device queue holding ONLY song-shaped positioned entries skips the deep fetch on a fresh multi-page book ask (the query-count pin shape PlayBookResumeTests already carries), plus a book-shaped entry still releases it.
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

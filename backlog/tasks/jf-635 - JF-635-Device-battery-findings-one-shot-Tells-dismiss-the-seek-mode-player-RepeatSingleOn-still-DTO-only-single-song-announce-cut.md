---
id: JF-635
title: >-
  JF-635 - Device battery findings: one-shot Tells dismiss the seek-mode player;
  RepeatSingleOn still DTO-only; single-song announce cut
status: To Do
assignee: []
created_date: '2026-09-25 15:31'
labels:
  - bug
  - platform
  - device-found
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the 2026-09-25 17:10-17:16 device battery (JF-625/JF-622/JF-326 verification round), same-turn rule.

Three findings from the battery's failing items, each with log-verified root cause:

1. ONE-SHOT TELLS DISMISS THE SEEK-MODE PLAYER (the big platform finding, tests 3+5): during VideoApp music playback, ANY new one-shot interaction whose response is a plain Tell (shouldEndSession=true, no VideoApp.Launch) appears to dismiss the playing video surface. Live evidence: 17:13:27 a FallbackIntent 'non ho capito' Tell closed the album playback; 17:14:53 the RateItem confirmation Tell closed it again (the rating itself was CORRECT). Consistent with the VideoApp surface being bound to the device's active-skill context: a new response without a video replaces it. Scope: document in CLAUDE.md (the Stop/Session Routing reference section); then EXPERIMENT with mitigations on device: (a) does shouldEndSession=false on the answer keep the player? (b) does re-appending a VideoApp.Launch of the same stream at the current offset work (needs the tracker position - offset would be approximate)? The honest baseline may be: in seek mode, Q&A one-shots interactions cost you the playback (pause survives because it carries AudioPlayer.Stop... verify).

2. REPEATSINGLEON (test 2, 'saltare la canzone' misroute + NoMediaPlaying): the NLU routed 'chiedi a mia collezione di saltare la canzone' to RepeatSingleOnIntent (not AMAZON.NextIntent, so our CannotNavigateMusicByVoice refusal never engaged); RepeatSingleOn then answered NoMediaPlaying because it is one of the SURVIVING DTO-only readers (session.FullNowPlayingItem check; a one-shot during playback carries a NEW empty session). Scope: (a) extend the JF-629 migration family to RepeatSingleOn/RepeatOff (and audit Loop/Shuffle handlers for the same shape) with the JF-632 medium gate; (b) check the it-IT model for a 'saltare/skip' one-shot phrasing that routes to NextIntent, or accept RepeatSingleOn as the entry and give IT the honest seek-mode refusal line.

3. SINGLE-SONG ANNOUNCE CUT IN SEEK MODE (test 4, 'magnolia'): the 17:14:07 response carried 'In riproduzione Magnolia' on the FINAL response (b36ed98f) and the fast-start player cut it exactly per the JF-501 shape; the album path's progressive-vehicle fix (AlbumPlayService) does not cover PlaySong's two launch sites. Scope: route the single-song announce through SpeakVideoLaunchAnnounceAsync like the album path (thread the request; the sites are the two LaunchSong call sites in PlaySongIntentHandler).
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

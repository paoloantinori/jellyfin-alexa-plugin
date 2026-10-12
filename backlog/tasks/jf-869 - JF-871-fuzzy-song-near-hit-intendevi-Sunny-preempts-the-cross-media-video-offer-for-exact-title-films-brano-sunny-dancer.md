---
id: JF-869
title: >-
  JF-871: fuzzy song near-hit ("intendevi Sunny?") preempts the cross-media
  video offer for exact-title films ("brano sunny dancer")
status: To Do
assignee: []
created_date: '2026-10-12 15:43'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Paolo's 2026-10-12 device round: "Alexa, chiedi a mia collezione di mettere il brano sunny dancer" (a FILM that exists in the library, no song/album/artist of that name) did NOT produce the JF-865 cross-media video offer. Log evidence (corr=a19f72ec, 17:39:23-27): PlaySong resolved song=sunny dancer, the song tier fuzzy-found the Bobby Hebb song "Sunny", and the skill answered the JF-116 near-match suggestion "Non ho trovato 'sunny dancer'... intendevi Sunny?" (disambig_matches=[{name:'Sunny'}]). The video fallback sits LAST in the cascade (song → artist → album → video) and fires only after a CONFIRMED miss; a sub-threshold fuzzy near-hit intercepts the cascade before the miss is declared. Net effect: for any title that fuzzy-matches some song, the exact-name video offer is unreachable on the first turn.

OPEN QUESTION to trace first (the deciding evidence): what does "no" to the intendevi-suggestion state do? Trace NoIntentHandler's suggestion-state arm: if "no" ends in plain song-not-found WITHOUT running the remaining cascade (artist → album → video), then JF-865's advertised scenario is dead for this whole class and the fix is to continue the cascade after the rejection; if "no" DOES continue and eventually offers the video, the defect is only first-turn (extra turn to reach the video) and the fix choice is ranking: an EXACT-title video match should outrank a sub-threshold fuzzy song near-hit (e.g. before prompting intendevi, run the video-title exact check when CrossMediaVideoSuggestion is not Off; offer video first or fold both into one prompt). Verify current CrossMediaVideoSuggestion mode on minix config (Confirm default?) before concluding the mode is not simply Off.

Also regression-guard the honest cases: a REAL near-miss song ask ("metti il brano sunni" intending Sunny) must still suggest the song, not offer a random video; a no-match-everywhere ask still not-founds cleanly.
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

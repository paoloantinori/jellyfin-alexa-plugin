---
id: JF-663
title: >-
  JF-663 - the playlist fuzzy surface is the last ungated kana leak:
  BuildPlaylistPlayResponseAsync plain-accepts a kana-origin query's romaji
  against Latin playlist names
status: To Do
assignee: []
created_date: '2026-09-28 18:32'
labels:
  - search
  - i18n
  - ja-JP
  - acceptance
dependencies: []
references:
  - backlog/tasks/jf-661*.md
  - backlog/tasks/jf-662*.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-28 same-turn from the JF-661+662 gate-marker review (finding 1, the reviewer's act-before-closing item; the sweep map's LEAVE #10 dispositions this surface BY SCOPE, not by safety).

THE GAP: BuildPlaylistPlayResponseAsync (AlbumPlayService ~:791) romanizes the playlist name (JF-643) and plain-accepts through SearchItemsFuzzyAsync over Playlist + FuzzyMatch + HandleFuzzyMiss auto-play with NO kana bar - the exact wrong-accept class JF-661 just closed one surface over (a kana-origin playlist query romanizing to a romaji string that plain-fuzzy scores >= 90 against an unrelated Latin-named playlist auto-plays silently).

THE WORK: the JF-660/JF-661 threading pattern at the playlist acceptance (the kanaOrigin flag captured on the raw playlist slot pre-romanization; the acceptance requires the length-banded DM collision via the shared PassesLengthBandedTitleCollision primitive or the honest playlist not-found); the sweep confirmed no other ungated surface remains in the family after this one.

VERIFICATION BAR: the live-shape pin (kana-origin query + a bait playlist titled like the romanized soup -> honest not-found, never a play) + the Latin control; the suite green; a live probe if the library carries playlists.
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

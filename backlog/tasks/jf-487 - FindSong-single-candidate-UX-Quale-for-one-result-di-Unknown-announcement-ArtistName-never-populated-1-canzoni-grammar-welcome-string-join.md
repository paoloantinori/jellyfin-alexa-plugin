---
id: JF-487
title: >-
  FindSong single-candidate UX: 'Quale?' for one result, 'di Unknown'
  announcement (ArtistName never populated), '1 canzoni' grammar, welcome-string
  join
status: Done
assignee: []
created_date: '2026-09-04 18:27'
updated_date: '2026-09-04 20:26'
labels: []
dependencies: []
references:
  - Device screenshot 2026-09-04 (the full exchange)
  - 'corr=c5293876 (the play, ''di Unknown'')'
  - FindSongSessionData attrs (ArtistName=null on the candidate)
  - HandleFuzzyMiss auto-play-at-90 precedent
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-04 device test (screenshot + logs, corr chain: FindSong session State=2 keywords 'idiot kings' → reply '1' → play corr=c5293876). The pause test itself PASSED (PauseIntent arrived, audio stopped); the complaint is the unnatural dialog that LED to the play. Three defects visible in the exchange:

1. SINGLE-CANDIDATE 'QUALE?' PROMPT: the skill said 'Ho trovato 1 canzoni. 1. The Idiot Kings. Quale?' - asking WHICH one when there is exactly ONE candidate. With a single match the skill should play it directly (the HandleFuzzyMiss auto-play-at-90 precedent; this candidate scored 105). The numbered-list + 'Quale?' flow is for 2+ candidates. The '1 canzoni' is also grammatically wrong (should be '1 canzone', singular) - the locale string uses a plural noun with a count format arg and no singular handling.

2. ARTIST 'Unknown' IN THE PLAY ANNOUNCEMENT: 'Ho trovato The Idiot Kings di Unknown. La riproduco.' The session's Candidates carried ArtistName=null (visible in the FindSongSessionData attributes: the candidate has Name and ItemId and Score but no ArtistName), so the announcement fell back to 'Unknown'. The SERVER-SIDE CARD shows the correct artist (Soul Coughing): the Jellyfin item HAS the artist metadata; the FindSong candidate-BUILDING code simply never populates ArtistName from the item. Fix: populate ArtistName at candidate-build time (from the item's Artists/AlbumArtists) so the play announcement names the real artist.

3. 'Benvenuto in Jellyfin SkillCosa posso riprodurre?' - missing separator between the welcome string and the follow-up question (a locale-string concatenation bug visible in the screenshot; check the it-IT ResponseStrings join site for the welcome message).

Acceptance criteria:
- FindSong with exactly one candidate above the auto-play threshold: plays directly with 'Riproduco <name> di <artist>' (no 'Quale?' prompt, no 'Ho trovato N canzoni' preamble for the single case).
- The play announcement names the real artist (populated from the item metadata at candidate build), never 'Unknown' when the item carries artist data.
- '1 canzone' vs 'N canzoni' grammatical number correct in all 17 locales (check each locale's pluralization shape).
- The welcome string concatenation fixed with a proper separator.
- Device re-verification: the same 'cerca idiot kings' flow plays directly with the artist named.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and deployed 2026-09-04 (commit 40a8f1ec). The dedup collapse to one unique name auto-plays at score >= max(userThreshold, 90) with the FindSongPlaying announcement; below the bar the JF-423 disambiguation prompt stays (different recordings share titles). ArtistName populated from item metadata (GetArtistSubtitle then AlbumArtists fallback) so candidates and announcements name the real artist instead of 'di Unknown'. FindSongFoundMultipleSingular added to all 17 locales (count grammar), welcome SSML separators repaired in all 17, LocaleStringsTests (36 cases) pins both. Simplify pass folded the two auto-play sites into a parameterized FoundOne and removed a dead local. Verified: full suite 3235/3235; live entry path via simulator (artist prompt unchanged by design); the second-turn branch is unit-verified (6 new tests) because the simulator cannot inject session attributes. Device test card item: 'cerca la canzone <title>' two-turn flow must auto-play the single candidate and announce the artist.
<!-- SECTION:FINAL_SUMMARY:END -->

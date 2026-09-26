---
id: JF-640
title: >-
  JF-640 - Podcast search: cross-type fuzzy acceptance plays music albums
  ('morning' -> Euphoria Morning -> Steel Rain) and the prefix tier is missing
status: In Progress
assignee: []
created_date: '2026-09-26 17:47'
updated_date: '2026-09-26 19:02'
labels:
  - bug
  - podcasts
  - device-found
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-636 device round (2026-09-26 19:19-19:20), same-turn rule. Paolo's first two podcast invocations 'failed' - the log shows why:

LIVE EVIDENCE: 'riproduci il podcast morning' resolved podcast_name=morning; the exact podcast search missed (the user's podcast is 'Morning Weekend'), the HandleFuzzyMiss fallback scored 'morning' vs 'Euphoria Morning' (the Chris Cornell ALBUM) at 90 and AUTO-ACCEPTED (>= 90 auto-plays), then GetPodcastEpisodes treated the album as a podcast container (shape=album is an accepted shape per the JF-373 design: MusicAlbum of Audio tracks is the community-plugin podcast shape) and played its newest track 'Steel Rain'. The user heard a song where they asked for a podcast.

TWO DEFECTS:
1. CROSS-TYPE FUZZY ACCEPTANCE: a podcast query accepting a MUSIC ALBUM at score 90. The fuzzy recall is doing its job; the acceptance gate is not type-aware. The JF-471 precedent (album-by-artist word-coverage free pass) judged cross-shape coincidences must not auto-play. The podcast search chain should only fuzzy-accept PODCAST containers (Series in a podcasts library, or MusicAlbums only when they actually live in a podcasts-type library / have podcast semantics), never a music album from the music library. Failing that, downgrade to the disambiguation prompt (the JF-377 honest shape).
2. PREFIX SEARCH GAP: 'morning' as a prefix of 'Morning Weekend' resolves in the TV NextUp core (series_name=morning worked at 19:20:19) but NOT in the podcast search path. The same short name should find the same series in both paths: add the NameStartsWith tiers to the podcast container search (the artist-search chain's shape) before the fuzzy fallback fires.

SCOPE: PlayPodcastIntentHandler's search chain (+ GetPodcastEpisodes' container validation if the type check belongs there); tests pinning: music-album fuzzy hit does NOT auto-play (prompts or not-founds), prefix hit DOES play the right podcast, exact name unchanged. Device-verify: 'riproduci il podcast morning' plays Morning Weekend.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-26 20:58 PREMISE CORRECTED + root cause nailed (second device round): the user's podcast IS named exactly 'Morning' (/data/media/podcasts/morning; the file listing confirmed it; the original 'Morning Weekend' reading was the EPISODE title). The slot carried the EXACT name and the play still went to Steel Rain. Live query reproduction: SearchTerm=morning + Series returns exactly ['Morning'] (the right answer!); SearchTerm=morning + MusicAlbum returns 4 albums (incl. Euphoria Morning). THE BUG: PlayPodcastIntentHandler queries MusicAlbum FIRST and only queries the Series shape when the album query returns ZERO (the JF-599 fallback chain). With any album matching the search term, the Series query never runs, so the exact-name podcast is unreachable; the flow then falls to SearchItemsFuzzyAsync where the album scores 90 and auto-accepts. FIX SHAPE (both needed): (1) run BOTH shape queries unconditionally (two cheap queries) and prefer the EXACT case-insensitive name match across the UNION before any fuzzy (exact beats fuzzy, Series exact beats album fuzzy); (2) the cross-type fuzzy guard from the original filing stands for the non-exact case (a podcast query must not auto-play a music album at 90 - prompt instead, the JF-377 shape). Tests: exact-name Series wins over album fuzzy-hit; album-only fuzzy-hit prompts instead of auto-playing; exact album (community-plugin shape) still auto-plays.
<!-- SECTION:NOTES:END -->

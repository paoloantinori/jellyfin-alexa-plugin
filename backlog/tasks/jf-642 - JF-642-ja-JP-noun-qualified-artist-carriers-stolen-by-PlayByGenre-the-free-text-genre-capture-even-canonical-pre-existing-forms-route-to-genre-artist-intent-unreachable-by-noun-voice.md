---
id: JF-642
title: >-
  JF-642 - ja-JP noun-qualified artist carriers stolen by PlayByGenre (the
  free-text genre capture): even canonical pre-existing forms route to genre,
  artist intent unreachable by noun voice
status: To Do
assignee: []
created_date: '2026-09-27 06:19'
updated_date: '2026-09-27 06:38'
labels:
  - nlu
  - ja-JP
  - device-found
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the JF-399 residue-pass live verification (same-turn rule).

LIVE EVIDENCE (profile-nlu on the live rebuilt model, two artists probed): ja-JP noun-qualified artist carriers are broadly stolen by PlayByGenreIntent - 'クイーン の曲を再生して' -> PlayByGenre{genre:'クイーン'}, 'ビートルズ の曲を再生して' -> PlayByGenre, 'ビートルズ の音楽を聴かせて' -> PlayByGenre. Even the CANONICAL pre-existing carrier ('{musician} の曲を再生して', present since before this pass) is stolen.

INVESTIGATION COMPLETE (2026-09-27, same night; all probes on the live model + minix simulator):

1. Model layer (all 17 locales enumerated): genre = AMAZON.Genre (built-in free-text) in 16 locales, AMAZON.SearchQuery in it-IT. Carrier-skeleton collisions with PlayArtistSongs exist in 15/17 locales (only fr-FR/fr-CA clean) - the latent shape is repo-wide, not ja-only.
2. Selection-level steal observed ONLY in ja-JP. Controls (profile-nlu, colliding carrier + artist name): en-US and hi-IN select PlayArtistSongsIntent (catalog-backed musician ER wins); es-MX, pt-BR, de-DE select PlaySongIntent with genre merely considered (benign: the cross-media artist fallback rescues). ja selects PlayByGenre every time, and 'ジャズ の曲を再生して' returned an EMPTY consideredIntents list - the artist intent is not even competing.
3. The ja steal is CARRIER-INDEPENDENT: 'クイーン のトラックを再生して' (a carrier PlayByGenre does not have) ALSO selected PlayByGenre{genre:'クイーン'} via slot-spanning. Carrier re-qualification alone cannot fix this; the free-text built-in slot swallows a prefix out of any shape.
4. The ja steal is SCRIPT-DEPENDENT: Latin 'queen の曲を再生して' routes correctly to PlayArtistSongsIntent{musician:'queen'} (AMAZON.Musician recognizes Latin names and wins selection); katakana loses. Japanese ASR transcribes foreign artist names as katakana, so the naturalized on-device form is the broken one.
5. End-to-end severity (minix simulator, ja-JP): genre='クイーン' -> NotFoundGenre (the JF-463 cross-media rescue FIRES but cannot cross the script gap); genre='ジャズ' -> NotFoundGenre (native-script genre values also fail: raw katakana never matches the library's Latin 'Jazz'); genre='Jazz' (Latin) -> plays a jazz track. PlayArtistSongs musician='クイーン' -> artist not-found. Net: katakana artist voice and katakana genre voice are BOTH broken end-to-end in ja; Latin transcriptions work.
6. Fix proof already live in the same probes: PlayMoodMusicIntent's custom Mood type considered クイーン and its authority answered ER_SUCCESS_NO_MATCH. A custom type cannot steal; a built-in free-text one does.

FIX DIRECTION (routing layer): convert the genre slot to a custom slot type with localized genre vocabulary - the JF-354/356 Mood precedent, applied to genre. Open scope decision for the worker: ja-only (divergence precedent: it-IT AlbumName, anti-pattern #10 note) vs all-17 (the collision skeleton is latent everywhere; en/it survive only via catalog-backed musician ER). Constraint: the handler reads the RAW slot value and passes it straight to Jellyfin's Genres filter, so a custom type needs either canonical values = library genre names with spoken synonyms, or a localized genre map in the handler (LocalizedMoodMap pattern).

COMPANION (separate mechanism, separate task JF-643): the search-layer script gap - katakana query values cannot fuzzy/phonetic-match Latin library names on ANY path (artist search, genre query, cross-media fallback; simulator-verified). Fixing routing alone still leaves naturalized ja artist voice at not-found.

PRIORITY: medium. ja-JP artist intent is effectively unreachable by naturalized voice today; genre queries in native script are equally dead.
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

---
id: JF-965
title: >-
  JF-963 alignment batch 1: by-order artist play (de/fr/es), start-the-album,
  album-shuffle, es artist name-carriers (lint-first)
status: To Do
assignee: []
created_date: '2026-11-25 14:55'
updated_date: '2026-11-25 21:43'
labels:
  - nlu
  - alignment
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-963 alignment batch 1: the maintainer-approved safe rows from the major-skill corpus (maintainer go 2026-11-25, 'si, tracciando tutto in un backlog prima'). REAL DELTA COMPUTED WITH THE CARRIERS TOOL (JF-970 AC #5, 2026-11-25, /tmp/jf965_candidates.tsv, 17 candidates run; zero SAME_CARRIER hits anywhere): ABSENT rows are the only work: (1) by-order artist: fr-FR 'Joue des chansons de {musician}' + 'Joue de la musique de {musician}' (the F3 indefinite-plural Joue family; ours is definite-only Lis les chansons de), de-DE 'Spiele Lieder von dem Künstler {musician}' + 'Spiele Musik von der Band {musician}' + 'Spiele Musik von der Gruppe {musician}' (the D3 carrier-noun rows); en-US DROPS OUT of item (1): 'play songs by {musician}' and 'play music by {musician}' are PRESENT (the vocabulary product), killing the A1 'we hold neither order' premise. (2) start-the-album: en-US 'start the album {album}' + 'start the record {album}' (A4), de-DE 'Starte das Album {album}'; fr-FR DROPS OUT: 'Lance l'album {album}' is PRESENT. (3) album-shuffle: en-US 'shuffle the album {album}' ONLY (the A2 artist-shuffle half 'shuffle music/songs by {musician}' is PRESENT via the product; the research missed that artist_media_verb includes 'shuffle'); per-locale album-shuffle wording is not corpus-pinned and stays out unless derived at execution from the locale's carrier conventions with the same carriers check. (4) es name-carriers, LINT-FIRST unchanged: 'reproduce canciones del artista/cantante/grupo {musician}' all three ABSENT; check the MediaInfoType synonym collision (the band/singer #46 class) BEFORE adding. TWELVE ABSENT rows total (fr 2, de 4 including Starte das Album, en 3, es 3; five candidates read PRESENT and drop out). BASELINE MECHANISM (unchanged, exists): two `python3 scripts/nlu-lab/cli.py run --live` passes with the battery workingset immediately before the deploy, `compare --expect-zero-changes` for the A/A floor, then post-deploy compare; catalog state certified by JF-971's canary when it lands (manual Gate 2 canary until then); the erosion signal is consideredIntents empty-to-non-empty on inverted-order and naked-artist forms; battery rows are the subsumption graph's NEW edges (graph diff-git + select) plus carrier-sharing sentinels. The 9-state ablation is NOT the primary design (keep it for unexplained reds). DEPENDS ON: JF-970 (landed: the carriers table above IS its AC #5), the capture part of JF-968 (landed: full-hypothesis rows), JF-971 (canary in run records), and JF-964 green for any en-US rows. EXECUTION DISCIPLINE per the adopted sequence, per row family: graph diff on a branch (stop on a new IDENTICAL edge; probe on a new SWALLOW edge with free/catalog slot) -> token lint + classify-fixtures on the branch -> lab round with the change-driven selection (card discipline per nlu-debug-protocol Gate 5) -> template edit + regenerate in ALL affected locales -> fixtures updated in the same change -> live verify after deploy. The probe-first list (noun-after playlist, open/go-to, give-me-a-suggestion, artist-radio, pon-a, par) is NOT in this batch: it stays in JF-963's notes pending its probes. The scartati (play some {genre} naked, Spiele das Lied, esegui) are never added.
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
SESSION 2026-11-25 sera (lab arc, pre-deploy): SCOPE now ELEVEN rows: the twelfth (en 'shuffle the album') moved to JF-974 because no current handler gives it honest semantics (PlayAlbum plays ordered; a model row alone would be a semantic lie; the content-scoped feature needs its own design: see JF-974's description for the full constraint set gathered this session).

GATE 1 re-verified in-session: carriers re-run on the final wording (/tmp/jf965_final_wording.tsv) reads 11/11 PRESENT on the named intents after the template edits; the es cantante row uses the task's pinned 'reproduce' verb for all three (the TSV's mixed 'pon' row and the task prose agree the carrier noun is the load-bearing part).

GRAPH DIFF (working tree vs main): exactly ONE new edge, fr-FR PlayByGenreIntent->PlayArtistSongsIntent SWALLOW via 'Joue de la musique {genre}' / 'Joue des chansons {genre}' swallowing the two new Joue rows (genre slot is free-classed). DISCIPLINE says probe: the lab battery carries the fr genre sentinels ('Joue de la musique rock' etc.) + the two candidate rows. All other graph movement is pre-existing edges gaining members (de PlayByGenre/PlaySong vs PlayArtistSongs +3, de PlayArtistSongs/PlaySong/PlayByGenre ->PlayAlbum +1 Starte row, en PlaySong/PlayByGenre ->PlayAlbum +2, es PlaySong->PlayArtistSongs +3). No new IDENTICAL edge; no stop condition.

TOKENS LINT: six new CARRIER_VS_VALUE findings, all the JF-955 issue-#46 class, all baselined deliberately (baseline diff is exactly +6; the 12 pre-existing stale hi-IN identities were left stale as on main, NOT swept into this change): de-DE BrowseCategory|kuenstler, de-DE MediaInfoType|band, de-DE MediaInfoType|kuenstler, es-ES MediaInfoType|artista, es-ES MediaInfoType|cantante, es-ES MediaInfoType|grupo (cantante/grupo are synonyms of the artist entry in the es template; de MediaInfoType carries Band/Kuenstler entries; de BrowseCategory owns kuenstler). Rationale: the es-ES baseline already carries 95 accepted members of this class incl. MediaInfoType|disco|PlayAlbumIntent; JF-955 tracks the class; the steal question is measured, not argued: MediaInfo sentinels added to de/es fixtures (Welche Band ist das / Erzaehl mir von dem Kuenstler / dime el nombre del artista / que grupo es este) and to the lab battery.

CLASSIFY-FIXTURES: zero classification diffs on all pre-existing fixture rows across fr/de/en/es (probed main vs branch with the real classifier); new candidate rows classify UNCONTESTED in en, CONTESTED in fr/de/es exactly where the graph predicted competitions; sentinels UNCONTESTED.

GATE-5 CARD (written before the lab round): QUESTION: do the eleven rows take their utterances without stealing neighbours? H1 (clean add): candidate rows route to their target intents; every sentinel row unchanged vs control. H2 (steal): >=1 sentinel flips (fr genre rows to PlayArtistSongs; de/es MediaInfo rows to PlayArtistSongs; en 'start the video' to PlayAlbum; PlaySong rows anywhere). H3 (no-op): candidate rows route as control (trainer does not pick the rows up). ARMS: control = main's committed model; change = branch templates. BATTERY: the graph select (148 rows: de 46, en 19, es 41, fr 42) + 11 candidate rows + 4 MediaInfo sentinels + en PlayVideo start sentinel. CONTROL RUNS FIRST AND LAST (drift check). DECISIONS: H1 -> merge candidate; H2 -> reword or drop the offending row (es 'banda' is the pre-analyzed non-colliding fallback noun) and re-run; H3 -> investigate trainer state before any conclusion (nlu_trainer_nondeterminism memory: rebuilds flip slot-filling).

LAB ROUND RESULTS (partial, updating live): fr H1: A/A 0/131, change 4/131 (candidates green in both battery sources; control misroutes were PlayRandomIntent/PlayMoodMusicIntent), drift 0/131. de H1: A/A 0/146, change 0/146 (candidates ALREADY correct on main via generalization; the rows pin them against trainer flips), drift 0/146. en H1: A/A 0/199, change 1 'error' row only (empty SMAPI response on 'play the playlist road trip'; re-probe run 20261125-220301 routes it PlayPlaylistIntent, 0 failures; candidates already correct via generalization; 3 pre-existing stable reds: give-me-music-by steal, What-am-I-watching, dinner-music). en build quirk: en-US lab builds twice exceeded the 600s wait while fr/de built in ~1min; the builds DO succeed (get-skill-status SUCCEEDED); workaround was skip-deploy probes against the sha-verified deployed model.

OPERATIONAL NOTE: the OS memory reaper killed the batched lab script once (20:51; cause: host Firefox ~9GB + qemu 2GB, not the lab); resumed as one-run-per-command. SMAPI stream kept strictly sequential throughout.

LAB ROUND COMPLETE: ALL FOUR LOCALES H1 (the clean-add hypothesis; zero steals anywhere; every control-first/last drift check zero). es-ES detail: control had the three candidate utterances at NO-SELECTION on main's model (dead air today, the strongest user-facing gap of the batch); change routes all three to PlayArtistSongsIntent with musician=queen (the carrier noun correctly excluded from the slot value); the MediaInfo sentinels (dime el nombre del artista / que grupo es este) hold on BOTH battery sources in the change arm; the six baselined JF-955-class collisions produce NO steal, measured. A/A 0/129; change 6/129 (exactly the candidate instances, all green); residual red only the pre-existing stable 'Reproduce jazz' PlaySong steal (2 instances). en control-3 drift 0/199. fr: 4/131 candidates green (control misrouted to PlayRandom/PlayMoodMusic). de: 0/146 (pinning only). Pre-existing control reds observed, all stable, none ours: fr (Repete->RepeatSingleOn, Reprendre->FollowMe, joue-la-saison->None), de (PlayLibrarySong-vs-PlaySong x2, spiele-den-song-streithoernschen->None x2, the JF-933 canary reading None on MAIN's model on the lab skill, worth a maintainer look), en (give-me-music-by steal, What-am-I-watching, play-dinner-music). Run ids: fr 192317/193327/194158/195207, de 200207/201404/202544/203730, en 211612/213059/214920(+reprobe 220301)/220330, es 221655/222604/223353(+control-3).
<!-- SECTION:NOTES:END -->

---
id: JF-342
title: >-
  Tolerate on-device NLU misroutes without wrong-artist false positives
  (language-agnostic fuzzy length guard)
status: Done
assignee: []
created_date: '2026-07-14 16:03'
updated_date: '2026-07-18 20:27'
labels:
  - bug
  - artist-search
  - fuzzy-matching
  - language-agnostic
dependencies: []
references:
  - /home/pantinor/.cc-mirror/zai/config/plans/sleepy-mapping-beacon.md
  - Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/ArtistSearch.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Why
When an Echo misroutes an album request to PlayArtistSongsIntent (e.g. "di mettere il disco jazz cafe" arrives as musician="disco jazz caffè"), tier-4 fuzzy-all matches a **wrong short artist ("Uazz")** and auto-plays it. A first fix shipped as `ContainsMediaNounCarrier` in PlayArtistSongsIntentHandler is REJECTED: it (a) hardcodes Italian vocabulary at the handler layer (ignores 16 other locales), and (b) blacklists content words ("disco", "album", "brano"...) that can be genuine artist/title tokens (e.g. the band *Disco Ensemble*). It treats a scoring-mechanism defect as a vocabulary defect — wrong altitude.

## Model routing is VERIFIED correct (not the cause)
profile-nlu against the deployed it-IT model (skill amzn1.ask.skill.33dfacd5-3676-4cdc-8b02-81efb227df83, stage development) on 2026-07-14 routes ALL of these to PlayAlbumIntent with album="jazz cafe": "di mettere il disco jazz cafe", "di mettere l'album jazz cafe", "metti l'album jazz cafe", "metti il disco jazz cafe", "chiedi a mia collezione di mettere il disco jazz cafe". So the album_noun article fix landed and the model is ruled out as the active cause. The on-device "Uazz" incident is profile-nlu↔on-device divergence (ASR + default-music-service competition), which profile-nlu cannot see. Conclusion: the model fix is the primary cure; the change in THIS task is defense-in-depth so that when the Echo still misroutes despite a correct model, the handler fails gracefully (clean "not found") instead of playing a random short artist.

## Root cause (verified by construction)
FuzzyMatcher.PartialRatio (Alexa/FuzzyMatcher.cs:314) slides a window the size of the shorter string across the longer one and keeps the best substring alignment. For query="disco jazz caffè" (16) vs candidate="Uazz" (4), the 4-char window lands on "jazz" → Levenshtein distance 1 (j→u) → score (4−1)*100/4 = 75. Default threshold is 60; BaseHandler.FuzzyMatch uses the Levenshtein-only FindBestMatch (NO phonetic bonus — confirmed). 75 ≥ 60 → "Uazz" returned as the sole tier-4 match; artists.Count==1 skips disambiguation and auto-plays. The existing maxLenDiff=32 guard does nothing (length diff is 12).

## Approach (fix the shared matcher, drop the blacklist)
1. Add a length-disproportion penalty helper to FuzzyMatcher.FindBestMatchWithScore — BOTH overloads (simple ~line 107, phonetic ~line 158). Apply right after `int score = PartialRatio(...)`, BEFORE the phonetic bonus in the second overload. See full plan file for the exact helper code.
2. Remove `ContainsMediaNounCarrier` + its call site from PlayArtistSongsIntentHandler.
3. Regression tests in FuzzyMatcherTests.cs: "disco jazz caffè" vs "Uazz" below threshold / FindBestMatch returns null; "la ballata del genesio" vs "Lamb" below threshold; preservation tests for "zepln"→"Led Zeppelin", "beetles"→"The Beatles", and contained short candidates.
4. Keep the it-IT album_noun article forms (routing root-cause fix, already shipped + verified).

## Safety (verified against the test suite)
All legitimate ASR-truncation matches have the candidate LONGER than the query ("beetles"→"The Beatles", "zepln"/"led zep"→"Led Zeppelin", "Beatls"→"The Beatles") and are exempt. The candidate-shorter-than-query-AND-not-contained branch is reached only by coincidental-substring false positives. The penalty uses a conservative ratio<0.5 trigger plus a containment exemption.

Full plan (with exact helper code, file paths, and verification commands): /home/pantinor/.cc-mirror/zai/config/plans/sleepy-mapping-beacon.md
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 ContainsMediaNounCarrier method and its call site are removed from PlayArtistSongsIntentHandler; no Italian content-word blacklist remains in any handler.
- [x] #2 FuzzyMatcher.FindBestMatchWithScore applies a length-disproportion penalty (candidate shorter than query, ratio<0.5, not contained → score scaled by ratio) in BOTH overloads, before the phonetic bonus.
- [x] #3 New regression tests prove "disco jazz caffè" no longer matches "Uazz" (FindBestMatch returns null, FindBestMatchWithScore score < DefaultThreshold) and "la ballata del genesio" no longer matches "Lamb".
- [x] #4 Existing ASR-truncation matches still pass: "zepln"→Led Zeppelin, "beetles"→The Beatles, "led zep"→Led Zeppelin, "Beatls"→The Beatles, and contained short candidates still score at containment.
- [x] #5 dotnet test (NO --no-build) passes with zero regressions; CI-matching container preflight (podman run ... dotnet test -c Release) also passes.
- [x] #6 Simulator on minix: PlayArtistSongsIntent musician="disco jazz caffè" → NotFoundArtist speech (logs show tier-4 matched=false); PlayArtistSongsIntent musician="pink floyd" still plays Pink Floyd; PlayAlbumIntent album="jazz caffè" still plays Jazz Cafe (score 88).
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-18 08:15
---
2026-07-18 (autonomous): implemented on branch jf-342-fuzzy-length-guard — added ApplyLengthPenalty (candidate shorter than half the query, not contained → score scaled by length ratio) to both FindBestMatchWithScore overloads (before the phonetic bonus) AND RankMatches. AC#1 already satisfied (ContainsMediaNounCarrier blacklist was never committed). TDD red→green: 3 rejection tests (Uazz, Lamb, score-below-threshold) failed before the fix, pass after; 2 preservation tests (ASR-truncation, contained) hold. Full suite 2522 pass, Release 0-warning. Adversarial code-review (opus): sound, safe to merge — proved penalty+phonetic can't resurrect a false positive (max 54 < 60), legitimate/ASR/contained matches preserved. PR #16 opened (validators pass, build-and-test/CodeQL pending). RESIDUAL: AC#6 (on-device simulator on minix) needs the user — cannot run autonomously; also a latent accent-variant edge (short accented names) with no live trigger today.
---

created: 2026-07-18 20:27
---
AC#6 ON-DEVICE VERIFIED (2026-07-18): deployed commit 796bf55 to minix (AlexaSkill_0.10.0.0 hot-swap, same AssemblyVersion so no config migration; config survived, JellyfinToken healthy — stream URL api_key present). Built-in Simulator (it-IT): (1) PlayArtistSongsIntent musician='disco jazz caffè' -> NotFoundArtist speech ('Spiacente, non ho trovato nessun artista...'), NO AudioPlayer.Play -> the 'Uazz' false-positive no longer auto-plays (the original incident is fixed); (2) PlayArtistSongsIntent 'pink floyd' -> AudioPlayer.Play with stream .../Audio/<id>/stream?static=true&api_key=da5a12a4... (legitimate match preserved + token healthy); (3) PlayAlbumIntent album='jazz caffè' -> AudioPlayer.Play (album plays). All 6 ACs met; task complete.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fuzzy length-disproportion penalty (FuzzyMatcher.ApplyLengthPenalty — candidate shorter than half the query and not contained => score scaled by length ratio) deployed and verified on-device. The original incident is fixed: PlayArtistSongsIntent 'disco jazz caffè' no longer auto-plays the wrong short artist 'Uazz' (returns NotFoundArtist), while legitimate matches ('pink floyd') and album routing ('jazz caffè') still play. Delivered via PR #16 (796bf55); unit tests (5: 3 rejection + 2 preservation) + adversarial opus review passed at merge; AC#6 on-device simulator check confirmed on minix after deploy.
<!-- SECTION:FINAL_SUMMARY:END -->

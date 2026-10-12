---
id: JF-869
title: >-
  JF-871: fuzzy song near-hit ("intendevi Sunny?") preempts the cross-media
  video offer for exact-title films ("brano sunny dancer")
status: Done
assignee: []
created_date: '2026-10-12 15:43'
updated_date: '2026-10-12 17:27'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
TRACE ANSWER (the deciding evidence, 2026-10-12 worktree jf871): 'no' to the intendevi suggestion state DEAD-ENDS. NoIntentHandler's song-suggestion arm reads DisambiguationHelper state (disambig_matches/disambig_index/disambig_type=song, written by HandleFuzzyMiss's Confirm branch); the decline path is: no resume state, no crossmedia_notfound_* keys (those exist only on cross-media OFFER asks), single-match list so nextIndex 1 >= matches.Count 1, and the answer is the generic NoMoreMatches Tell. The remaining cascade (artist, album, video) NEVER runs, and even the honest NotFoundSongByName is not spoken. Both defects are real: first-turn interception AND the dead-end.

FIX LANDED (branch jf871): (1) RANKING: HandleFuzzyMiss gains an optional confirmAlternativeAsync seam consulted only in the Confirm branch before the intendevi ask is built; PlaySong passes CrossMediaFallback.TryVideoFallbackAsync with the new exactOnly flag (exact-name equality via the shared JF-420.1 predicate ArtistSearch.IsExactNameMatch over tier-1 SearchTerm candidates, fuzzy tier skipped), no-musician shape only, so an EXACT-title video is offered BEFORE the sub-threshold song near-hit prompt in Confirm mode and launches directly in AutoServe; a fuzzy-only video does NOT preempt (guard: 'sunni' still suggests the song). (2) DEAD-END: the rejected suggestion now counts as a song-tier miss: the intendevi ask carries song_suggestion_query (the PRE-romanization query, so the decline re-derives the kana-origin flag) through HandleFuzzyMiss's new sessionExtras param (extra keys join the ask's ACTIVE set; registered as ConversationalFlows.SongSuggestionKeys so any newer flow's ask supersedes them); NoIntentHandler gained optional cascade collaborators (DI-injected in production) and its decline branch runs the shared BaseHandler.RunSongMissCascadeAsync, the ONE extraction of PlaySong's artist-then-album-then-video-then-not-found tail, ending in NotFoundSongByName when everything misses instead of NoMoreMatches.

RESIDUALS: (a) the musician-scoped intendevi decline keeps today's NoMoreMatches (the cascade legs are no-musician-only; a musician decline would need the artist name stored, out of scope, pinned by MusicianScopedSuggestion_CarriesNoDeclineContext); (b) PlayAlbumIntentHandler does NOT have the interception shape to fix: its miss tail is artist fallback then NotFoundAlbumByName, there is NO video tier there at all (JF-865 wired the video leg at PlaySong only), so an exact-title film asked as an album dead-ends the same way but the fix belongs to the JF-864 deferred lane (wiring the video leg at the other steal callers: PlayByGenre en-AU/es-ES, PlayArtistSongs ja, and PlayAlbum if it ever steals bare movie asks); (c) the JF-116 intendevi prompt itself is unchanged for every other HandleFuzzyMiss caller (the new params default null).

TESTS: SongSuggestionVideoPreemptionTests (11 pins: near-miss song still suggested, exact-title video offer preempts, AutoServe launches, Off keeps the prompt, fuzzy-video two-turn contract decline-then-offer, decline nothing-anywhere ends NotFoundSongByName, null-collaborator fallback to NoMoreMatches, list ask carries no decline context, musician scope, flow supersession markers, miss cascade clean not-found). Suite 5802/5802 BOTH TFMs (net9.0 + net10.0) after the change; build 0 warnings.

MODE VERIFIED: live minix config XML reads DefaultCrossMediaArtistSuggestion=Confirm (podman exec grep, read-only, 2026-10-12) and the code default is Confirm (PluginConfiguration.cs:309); the mode was NOT Off, so the interception is the real cause, not a disabled tier.

GATES (2026-10-12): /simplify ran 4 parallel cleanup agents; applied: single null-handling site for sessionExtras in HandleFuzzyMiss's Confirm branch, shared noMusicianShape local in PlaySong, NoIntentHandler ctor collaborators made REQUIRED mirroring the YesIntentHandler twin (6 legacy test constructions updated, null-fallback + its pin test deleted), TestHelpers.CreatePlaySongIntent reuse in the musician test, GetAttr collapsed to the GetValueOrDefault idiom. Skipped: RecordingPlaySongHandler/IsVideoQuery/CreateSongIntent/NotFoundSongByNameEnUs test-helper hoists (second copies, below the house third-copy rule; sibling suites repoint when touched per the JF-781 pattern) and the named-predicate simplification (CS0165 definite assignment, false positive). /code-review high ran as a forked skill; applied: the decline turn now sends the same SearchingMedia progressive ping PlaySong sends before the identical cascade (latency feedback), a ja-JP mirror pin test (stored decline context is the raw kana; the decline's honest miss speaks the re-romanized query), and one banned hyphen-in-prose fix in ConversationalFlows.

CODE-REVIEW SKIPS (recorded, not applied): (1) FuzzyMatchBehavior.AutoPlay users bypass the exact-video preemption by their own opt-in (the auto-accept disjunct returns before the Confirm seam): preempting their auto-play with a Confirm ask would violate the never-prompt contract, judged the knob semantics not a defect; (2) the shared 2-content-word cross-media guard also bounds the exactOnly preemption, so 3+ content-word exact titles stay unreachable (the JF-865 pin PlaySong_Miss_WordGuardThreeContentWords_NoVideoQuery commits the guard ON the video leg; relaxing it for exact-only re-weighs a committed pin and widens blast radius, residual left for a dedicated decision); (3) RunSongMissCascadeAsync's 14-param surface (four collaborator params duplicated as NoIntentHandler fields) noted as surface, not defect; a SongMissCascade collaborator is the house pattern if a third caller appears.

FINAL EVIDENCE: dotnet build Debug + Release both succeed 0 warnings; dotnet test 5802/5802 on BOTH TFMs (net9.0 + net10.0) after all gate fixes.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf871 (3ba7faa6), merged to main 4493ae44, pushed; deploy rides the next batch. TRACE ANSWER: the intendevi decline DEAD-ENDED (NoIntentHandler's suggestion-state arm found no crossmedia keys and nextIndex 1 >= 1 matches -> generic NoMoreMatches Tell; cascade never ran; both defects real; live minix config verified Confirm, so mode was not the explanation). FIX: (1) HandleFuzzyMiss optional confirmAlternativeAsync in the Confirm branch; PlaySong passes TryVideoFallbackAsync exactOnly (JF-420.1 exact-name predicate over tier-1 SearchTerm candidates, fuzzy tier skipped, flag/mode/word-guard shared) so an exact-title video preempts a sub-threshold song near-hit while a fuzzy-only video does not; (2) the ask carries song_suggestion_query (pre-romanization, kana flag re-derivable) via sessionExtras + ConversationalFlows.SongSuggestionKeys (superseded by any newer ask), and the decline runs the shared RunSongMissCascadeAsync (artist, album, video, NotFoundSongByName tail, SearchingMedia ping). 11 pins in SongSuggestionVideoPreemptionTests; JF-865 battery green; album tier verified NOT to carry the shape (video leg exists at PlaySong only; residual belongs to the deferred wiring lane). Gates: /simplify (5 applied; helper-hoist skips per the third-copy rule) + /code-review high (3 applied; 3 skips dispositioned: AutoPlay-fuzzy bypass is the user's own opt-in, the 2-word guard bounds 3+-word exact titles per the committed JF-865 pin, the 14-param cascade surface noted for a future collaborator at a third caller). Tests 5802/5802 BOTH TFMs; Debug+Release 0 warnings.
<!-- SECTION:FINAL_SUMMARY:END -->

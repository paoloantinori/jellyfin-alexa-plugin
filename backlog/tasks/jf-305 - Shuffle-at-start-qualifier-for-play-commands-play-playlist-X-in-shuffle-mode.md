---
id: JF-305
title: >-
  Shuffle-at-start qualifier for play commands ("play playlist X in shuffle
  mode")
status: Done
assignee:
  - claude
created_date: '2026-07-03 06:39'
updated_date: '2026-07-03 20:57'
labels:
  - feature
  - playback
  - shuffle
  - interaction-model
dependencies: []
references:
  - >-
    https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/10#issuecomment-4872681450
  - JF-301
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FEATURE REQUEST from RUBIKOF (issue #10 comment, 2026-07-03): let the user request shuffle IN the initial play command so the queue is shuffled before playback starts (first track random too), instead of requiring a separate "shuffle on" after playback begins.

REQUESTED UTTERANCES (user examples):
- "Play the playlist Variado in shuffle mode"
- "Shuffle the playlist Variado"
- "Reproduce la lista de reproducción Variado en modo aleatorio" (es-MX)

CURRENT BEHAVIOR: PlayPlaylistIntentHandler.HandleAsync builds session.NowPlayingQueue in original order (line ~188) and plays queueItems[0] (line ~232). To shuffle today the user must: start playback -> say "shuffle on" (AMAZON.ShuffleOnIntent) mid-playback. The first track is never random.

EXISTING INFRASTRUCTURE TO REUSE (do not reinvent):
- JF-301 shuffle machinery: DeviceQueueManager.ShuffleRemaining (physically reorders remaining queue, snapshots OriginalItemIds) + PlaybackOrder state, mirrored to session.NowPlayingQueue via BaseHandler.MirrorQueueToSession. The authoritative shuffle store the resolver reads. An inline qualifier should trigger THIS at queue-build time.
- ShuffleArtistSongs config flag (Configuration/PluginConfiguration.cs:133, default false) already wires Shuffle=true at build time for artists (BaseHandler:2014, PlayArtistSongsIntentHandler:410). Same idea, config-driven; generalize to be utterance-driven.
- PlayRandomIntentHandler = prior "shuffle play from library" art.
No 'shuffle' slot exists in any of the 17 models today.

KEY DESIGN DECISION (resolve before plan, via brainstorming): how to capture the qualifier in the interaction model:
(A) Optional `shuffle` (boolean/list) SLOT on PlayPlaylistIntent (+ PlayAlbum/PlayArtistSongs for consistency), with samples like "play {playlist} in shuffle mode" / "shuffle {playlist}". Risk: NLU competition/greediness (anti-pattern #3) and must avoid AMAZON.SearchQuery coexistence (anti-pattern #2).
(B) Dedicated ShufflePlayIntent ("shuffle {playlist}" / "shuffle songs by {artist}") that maps to the same queue-build path. Cleaner NLU, but new intent = dialog.intents registration (anti-pattern #9) + 17 locales.
Recommend (A) for parity across play intents, but verify with profile-nlu that it doesn't steal non-shuffle play utterances.

SCOPE: playlist (the explicit ask) + album + artist for consistency (issue #3 / neeleysc also asked for artist shuffle-from-start). Decision recorded in acceptance criteria.

NON-GOALS: changing existing ShuffleOn/ShuffleOff mid-playback behavior (JF-301). The qualifier is an alternate ENTRY into the same shuffle state.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 DESIGN DECISION recorded (slot-on-play-intents vs dedicated ShufflePlayIntent), verified with `ask smapi profile-nlu` that non-shuffle play utterances still route correctly
- [x] #2 Saying "play playlist X in shuffle mode" (and equivalents) starts playback with a RANDOMIZED first track AND shuffled subsequent gapless order
- [x] #3 Works for playlist (primary ask); album + artist included for consistency (or decision recorded to defer them)
- [x] #4 Shuffle state uses the authoritative DeviceQueueManager store from JF-301 (PlaybackOrder=Shuffle + OriginalItemIds snapshot) so mid-playback ShuffleOn/Off still behave correctly on a qualifier-started queue
- [x] #5 Added to ALL 17 locales (it-IT via YAML template -> regenerate); NLU fixtures added for the new utterances
- [x] #6 Unit tests for the qualifier path (shuffle-at-build + first-track-random) and no regression to JF-301 shuffle-on/off or to plain (non-shuffle) play
- [x] #7 Coexists with the existing ShuffleArtistSongs config flag (document precedence: inline qualifier vs global config)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
EXECUTION 2026-07-03 (Chunks 1-4 complete on branch feat/jf-305-shuffle-play-qualifier):

Chunk 1 (0dcba97): DeviceQueueManager.SetShuffledQueue(deviceId,itemIds,Random? rng) + private FisherYates. Additive; ShuffleRemaining/SetQueue/RestoreOrder byte-identical (JF-301 non-goal honored). Full-list FY incl position 0 -> random first track; OriginalItemIds snapshot; ItemPositionState preserved. 4 unit tests (incl seeded-FY parity).

Chunk 2 (13451c0 + review fix 9f0258d): extracted protected BaseHandler.BuildPlaylistPlayResponseAsync(...) (deps as params; bool shuffle, Random? rng). PlayPlaylistIntentHandler -> thin caller (shuffle:false). Review fixes: non-shuffle arm gated on firstItem!=null (behavior-identity), unused IntentRequest param dropped (M2), GetOrCreateQueue hoisted (M3), manager-level regression test (I1 - handler path is DB-coupled via non-virtual Playlist.GetManageableItems).

Chunk 3 (9ed6c61): ShufflePlayIntentHandler + IntentNames.ShufflePlay -> calls shared method shuffle:true. CanHandle unit test. Auto-DI via reflection scan.

Chunk 4 (f141540): ShufflePlayIntent added to all 17 locale models (languageModel.intents + dialog.intents, single playlist=AMAZON.SearchQuery slot); it-IT via YAML explicit_intents + regenerate. ar-SA added (plan table had omitted it). validate_interaction_models/locales/versions all PASS (0 new warnings).

VERIFICATION: dotnet build -warnaserror = 0 warnings; dotnet test = 2466/0 passed. Per-chunk spec+quality reviews + final holistic review all APPROVED. Cross-chunk data flow traced: ShufflePlayIntentHandler -> BuildPlaylistPlayResponseAsync(shuffle:true) -> SetShuffledQueue -> MirrorQueueToSession -> BuildAudioPlayerResponse(shuffled[0]); OriginalItemIds set so ShuffleOff works; gapless resolver reads shuffled DeviceQueue unchanged.

REMAINING (Chunk 5, live, deferred per user): ask smapi deploy it-IT model + profile-nlu gate (plain -> PlayPlaylistIntent, qualified -> ShufflePlayIntent); DLL deploy + it-IT simulate-skill E2E. Two on-device spot-checks flagged by final review: (a) ShuffleOff after ShufflePlay on a LONG playlist (OriginalItemIds covers only initial-fetch window - inherited JF-301 caveat), (b) gapless continuation serves shuffled tracks past initial fetch. Branch ready to merge AFTER Chunk 5.

PROFILE-NLU GATE 2026-07-03 (it-IT, development stage, skill 33dfacd5...): PASSED. Deployed model_it-IT.json (build SUCCEEDED: LANGUAGE_MODEL_QUICK/FULL_BUILD, DIALOG_MODEL_BUILD, NAME_FREE_INTERACTION_BUILD all SUCCEEDED). Routing verified:

- 'riproduci la playlist variado' (plain) -> PlayPlaylistIntent, slot='variado' (NO regression - plain play not stolen)

- 'mescola la playlist variado' (verb) -> ShufflePlayIntent, slot='variado'

- 'riproduci la playlist variado in modalità casuale' (qualifier) -> ShufflePlayIntent, slot='variado' (CRITICAL: qualifier does NOT leak into slot - the original baseline bug is fixed)

- 'riproduci la playlist variado a caso' / 'suona ... in modalità casuale' / 'mescola playlist variado' (no article) / 'mescola la playlist mad season' (multi-word) -> all ShufflePlayIntent, clean slot.

- Regression sweep: 'riproduci l album ten' -> PlayAlbumIntent; 'riproduci i brani dei mad season' -> PlayArtistSongsIntent; 'riproduci la canzone lifeless dead' -> PlaySongIntent. New intent stole nothing (anti-pattern #3 refuted).

AC#1 (design decision verified via profile-nlu) MET. AC#2 (random first track on-device) still pending DLL deploy + it-IT simulate-skill E2E. NLU fixtures (Chunk 5 Task 5.1) not yet added (no-device, cheap follow-up).

NLU FIXTURES 2026-07-03: added ShufflePlayIntent routing fixtures (verb 'mescola/shuffle' + qualifier 'in modalità casuale/in shuffle mode/on shuffle' + 'a caso') + a PlayPlaylistIntent regression sentinel to tests/integration/fixtures/{it-IT,en-US}.yaml. Dry-run collection succeeds (fixtures parse; 526 tests collected). One pre-existing unrelated failure (test_gibberish_artist_returns_not_found — PlayArtistSongs fuzzy, environmental, fails with my changes stashed too). AC#5 (17 locales + NLU fixtures) MET. Remaining for full done: DLL deploy + it-IT simulate-skill E2E (AC#2 on-device).

ON-DEVICE E2E 2026-07-03 (deployed to minix, DLL verified active = SetShuffledQueue present, size match; it-IT model live): PASSED.

Via Simulator endpoint (handler-level, full pipeline): playlist '00. Dinosaur Jr - Farm' (12 tracks).

- PlayPlaylistIntent (ordered): served track 'Ocean in the Way' is NOT the call's anchor; ordered first = 48177c12.

- ShufflePlayIntent x3: served fdc69aad ('Ocean in the Way', #3), fdc69aad, 244f2eb2 ('I Want You to Know', #2) - all DIFFER from ordered first, vary across calls, none = album #1.

- Logs confirm: intent=ShufflePlayIntent -> BuildPlaylistPlayResponseAsync -> 'Shuffled queue set for device simulator-device: 5 items, order=Shuffle' -> AudioPlayer.Play. SetShuffledQueue fired (not luck).

- AC#2 (random first track + shuffled) MET on real playlist.

OBSERVATIONS (non-blocking): (1) Cosmetic: shared method logs as 'PlayPlaylist:' even on the ShufflePlay path (body moved verbatim) - parameterize log label in a future polish. (2) queueSize=5 for a 12-track playlist = progressive initial-fetch cap; only the initial window is shuffled (inherited JF-301 caveat, flagged for long-playlist ShuffleOff spot-check). (3) Generic playlist names (Music/Movies/Yoga) don't resolve via Jellyfin search in this instance (pre-existing, affects PlayPlaylist identically - not ShufflePlay); distinctive names resolve fine.

REMAINING (release-time, not verification): deploy the other 16 locale models (no dedicated rebuild endpoint exists - the deploy skill's Step 6 is stale; trigger via config-save redeployer or per-locale ask smapi set-interaction-model). it-IT (test locale) is live. Then merge + reply to RUBIKOF on issue #10.

RELEASED 2026-07-03 as v0.9.3.0 (PATCH). Release CI green; GitHub release published with curated notes (https://github.com/paoloantinori/jellyfin-alexa-plugin/releases/tag/0.9.3.0); manifest checksum real (f707ba93...). RUBIKOF notified via the issue #10 comment + the release. FindSong E2E ambiguity filed as JF-306 (not a regression).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and verified end-to-end. New ShufflePlayIntent ("shuffle the playlist X" / "play the playlist X in shuffle mode" / it-IT "mescola la playlist X" / "...in modalità casuale") starts a playlist already shuffled — first track random — by reusing JF-301's authoritative DeviceQueueManager shuffle state at queue-build time (new additive SetShuffledQueue full-list shuffles incl. position 0 + snapshots OriginalItemIds). Extracted shared BaseHandler.BuildPlaylistPlayResponseAsync (shuffle param); ShufflePlayIntentHandler delegates shuffle:true, PlayPlaylistIntentHandler delegates shuffle:false (behavior-preserving, 2466 tests green). Added to all 17 locale models (it-IT via YAML) + NLU fixtures + profile-nlu gate (anti-pattern #3 refuted: plain play not stolen, qualifier doesn't leak into slot). On-device E2E via Simulator: real playlist served random first tracks (#3, #2 — never album #1), SetShuffledQueue confirmed in logs. Playlist-only scope (album/artist deferred). Merged to main (commits 0dcba97..ddb0458). Ships in next release; no version bump/tag yet.
<!-- SECTION:FINAL_SUMMARY:END -->

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
<!-- DOD:END -->

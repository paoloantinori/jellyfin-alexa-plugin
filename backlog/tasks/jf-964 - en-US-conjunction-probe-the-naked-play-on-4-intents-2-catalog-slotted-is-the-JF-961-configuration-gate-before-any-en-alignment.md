---
id: JF-964
title: >-
  en-US conjunction probe: the naked 'play {#}' on 4 intents (2 catalog-slotted)
  is the JF-961 configuration; gate before any en alignment
status: Done
assignee: []
created_date: '2026-11-25 14:55'
updated_date: '2026-11-26 17:36'
labels:
  - nlu
  - catalog-gate
  - probe
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fable's JF-963 review point 5 (2026-11-25): en-US carries the naked 'play {#}' carrier on FOUR intents, two of which consume catalog-backed slots (PlayArtistSongsIntent via JellyfinArtist on the musician slot; PlayBookIntent via AudiobookTitle) — the exact wiring configuration the JF-961 ablation proved produces the conjunction Fallback on es-US (artist wiring + at least one more catalog wiring breaks reproduce-shaped selection on non-famous titles). Before ANY en-US alignment rows land (JF-965 and later batches), probe live whether en-US already has the disease: (1) 'play the song <non-famous title>' x2 probes (pick a real library song title that is not famous), (2) 'play <non-famous title>' x2 (the naked form), (3) verb-variant control ('listen to <same title>' x1), (4) evergreen control ('play songs by pink floyd' x1). Reading per the ablation's matrix: Fallback on the carrier form + green on the verb variant = the conjunction is live in en-US too (file the finding; alignment rows in en-US then need the same conjunction-awareness as es); all green = en-US is currently healthy (the en sample density may be absorbing it) and alignment can proceed with the standard graph-diff sequence only. Cheap: ~6 profile-nlu calls.
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
GATE-5 CARD (orchestrator closure round, 2026-11-26, written before the run):

Question: does en-US carry the JF-961 conjunction disease (catalog-wiring conjunction breaking reproduce-shaped selection on non-famous titles)?

H1 conjunction live: carrier+naked non-famous rows Fallback; verb-variant control green; evergreen/famous green.

H2 healthy (en sample density absorbs it): all rows route (naked row may arbitrate to any of the four carrier intents; green = not Fallback, record winner).

Gate-1 premise re-verified by tool this sitting (carriers subcommand): FOUR naked 'play {#}' carriers in en-US, two catalog-slotted (play {musician} @ PlayArtistSongsIntent/JellyfinArtist, play {book} @ PlayBookIntent/AudiobookTitle), plus Play {genre} @ PlayByGenreIntent and play {song} @ PlaySongIntent (static template marks both not catalog-backed; the household SongTitle catalog may add a third live wiring).

Gate-2 live state: deployed assembly 1.1.2.0 loaded 16:34:09 today (podman log line), HEAD docs-only since, so deployed DLL == HEAD code; no rebuild triggered since; catalog canaries ride in the battery (asymmetric: a failed canary makes the session a catalog-state session, reds not model evidence).

Battery (9 rows, one --live run, label jf964-en-conjunction): pink floyd evergreen FIRST (canary), 'play the song confessioni di un cuoco criminale' x2 (non-famous carrier; real library title, Gatto Ciliegia contro il Grande Freddo), 'play confessioni di un cuoco criminale' x2 (naked), 'listen to confessioni di un cuoco criminale' x1 (verb control), 'play the song in bloom' x1 (song canary + famous control), 'play the book the upside of irrationality' x1 (audiobook canary), pink floyd evergreen LAST (drift control).

Decision map: all green = H2, close with verdict 'no conjunction; en alignment proceeds with the standard graph-diff sequence only'. Carrier Fallback + verb green = H1, file finding, en rows need conjunction-awareness. Any canary red = catalog-state session, no model verdict. Famous red + non-famous green = surprise, investigate before verdict.

RESULTS (run 20261126-175416-jf964-en-conjunction-r2, 187 rows incl. full fixture battery, JF-973 double-run makes --live full runs ~40min: do NOT diagnose as stalled before ~45min; run 1 at 17:20 was healthy and was killed in error, quarantined to /tmp/jf964-stalled-run-20261126-172025.json):

All 9 diagnostic rows routed, zero Fallback: non-famous carrier x2 PlaySongIntent, naked x2 PlaySongIntent, verb control PlaySongIntent, famous in-bloom PlaySongIntent, pink-floyd bookends PlayArtistSongsIntent (ER_SUCCESS_MATCH JellyfinArtist, direct probe 17:52), upside-of-irrationality PlayBookIntent.

Live en-US PlaySong song slot is FREE-TEXT (raw: AlexaEntities only, ER_SUCCESS_NO_MATCH) so the wiring premise stays at exactly two catalog wirings (artist, audiobook).

3 fixture expectation failures observed (coldplay-artist row -> PlaySong carrier-flip class; What-am-I-watching -> MediaInfo; play-dinner-music -> PlayNext): orthogonal fixture hygiene, filed for the regular triage path, not conjunction evidence (famous rows green).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED via recorded live run 20261126-175416-jf964-en-conjunction-r2 (187 rows, --live, real skill amzn1.ask.skill.33dfacd5): VERDICT H2, en-US is HEALTHY. All diagnostic rows green: non-famous carrier 'play the song confessioni di un cuoco criminale' x2 -> PlaySongIntent; naked 'play confessioni...' x2 -> PlaySongIntent; verb control 'listen to ...' -> PlaySongIntent; famous control 'play the song in bloom' -> PlaySongIntent; artist canary 'play songs by pink floyd' bookends -> PlayArtistSongsIntent (artist-catalog ER_SUCCESS_MATCH on JellyfinAuthoritin verified by direct probe same sitting); audiobook canary 'play the book the upside of irrationality' -> PlayBookIntent. ZERO Fallback/NO_SELECTION in the diagnostic set: the JF-961 conjunction disease is NOT live in en-US under the current wiring (artist + audiobook catalog-wired; the live PlaySong song slot is FREE-TEXT: raw payload shows only the AlexaEntities builtin authority, ER_SUCCESS_NO_MATCH, so no third song wiring). Decision per the card: en-US alignment rows may proceed with the standard graph-diff sequence only, no conjunction-awareness needed. Incident correlation confirmed: the 2026-11-25 NONE readings were the dead catalogs, not a model defect. Orthogonal observations recorded, not blockers: 3 fixture expectation failures in the same run ('give me music by coldplay' PlayArtistSongs->PlaySong, 'What am I watching' InProgressMediaList->MediaInfo, 'play dinner music' PlayMoodMusic->PlayNext); first is consistent with the armed-SongTitle carrier-flip class, all three are fixture-hygiene items for the regular triage path. Canaries rode as routing rows (NLU_CANARY_SNAPSHOT not armed; deliberate maintainer choice). gate-exempt: probe/closure task, no code or model change (build/test/simplify/code-review DoD items not applicable).
<!-- SECTION:FINAL_SUMMARY:END -->

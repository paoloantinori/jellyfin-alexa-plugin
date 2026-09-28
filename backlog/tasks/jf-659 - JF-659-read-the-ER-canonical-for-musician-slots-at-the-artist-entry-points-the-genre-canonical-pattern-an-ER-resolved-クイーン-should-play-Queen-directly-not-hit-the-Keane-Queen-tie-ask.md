---
id: JF-659
title: >-
  JF-659 - read the ER canonical for musician slots at the artist entry points
  (the genre-canonical pattern): an ER-resolved クイーン should play Queen directly,
  not hit the Keane/Queen tie ask
status: To Do
assignee: []
created_date: '2026-09-28 04:29'
labels:
  - nlu
  - search
  - i18n
  - ja-JP
dependencies: []
references:
  - >-
    backlog/tasks/jf-646 -
    JF-646-catalog-side-katakana-synonyms-Latin-artist-album-names-get-kana-variants-in-the-ja-catalog-upload-so-NLU-selection-resolves-naturalized-ja-JP-voice-the-routing-layer-complement-to-JF-643.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-28 from the JF-646 live verification battery (the arc's final state; everything else passed).

THE FINDING: naturalized ja artist voice now SELECTS PlayArtistSongs with ER_SUCCESS_MATCH on the JellyfinArtist catalog (クイーン の曲を再生して verified, deterministic), but the HANDLER still resolves from the RAW musician slot (クイーン), romanizes to 'kuin', and hits the Queen/Keane DM tie -> the multi-artist disambiguation ask fires even when Amazon's ER already resolved クイーン to the canonical 'Queen'. One extra conversational turn on every tie-shaped name despite a resolved entity.

THE WORK: read the ER canonical at the artist entry points, mirroring the genre canonical read JF-642 shipped for the genre slots (SlotValueHelper.GetCanonicalValue(slot) ?? raw). Sites: PlayArtistSongsIntentHandler's musician slot, the JF-471 album-by-artist acceptance, CrossMediaFallback's artist fallback entry (where the slot object is available), and PlaySong/PlayVideo/PlayBook/PlayPodcast musician slots (same one-line shape; raw kept for speech per the JF-642 F-1 lesson). With a canonical in hand the JF-652 acceptance resolves directly (exact tag match; no tie scan needed).

VERIFICATION BAR: the simulator-equivalent shape through a slot WITH resolution resolves to the canonical artist without the tie ask (unit-test the handler with an ER-carrying slot); the raw-slot path (simulator, no ER) keeps today's honest ask; the Latin matrix unchanged.
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

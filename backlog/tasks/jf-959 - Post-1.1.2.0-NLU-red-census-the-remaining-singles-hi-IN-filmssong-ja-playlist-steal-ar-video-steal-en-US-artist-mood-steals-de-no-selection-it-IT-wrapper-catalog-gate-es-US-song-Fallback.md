---
id: JF-959
title: >-
  Post-1.1.2.0 NLU red census: the remaining singles (hi-IN films+song, ja
  playlist steal, ar video steal, en-US artist/mood steals, de no-selection,
  it-IT wrapper catalog-gate, es-US song Fallback)
status: Done
assignee: []
created_date: '2026-11-24 20:13'
updated_date: '2026-11-25 10:56'
labels:
  - nlu
  - triage
  - census
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The post-1.1.2.0 fresh-suite red census (2026-11-24, 1365 passed / 79 failed / 54 skipped, 3h04m) beyond the families already dispositioned elsewhere (the it-IT song-carrier catalog flips re-pinned in b318376a; the es episode steal tracked in JF-958; the es/nl skip+reminder structural class extending JF-953; the simulate e2e families incl. the known per-locale outage class). Remaining singles with their measured actuals, each needing its own probe round: (1) hi-IN: four film rows ('film inception dekho' word orders) read Fallback (PlayVideoIntent expected; MovieTitle not declared in hi-IN so the catalog is not the obvious cause) and 'gana shahar ki awaz chalao' reads BrowseLibraryIntent (PlaySongIntent expected). (2) ja-JP: 'shuffle shirotsumekusa' reads AddSongToPlaylistIntent (PlaySongIntent expected), a playlist-carrier steal in Japanese. (3) ar-SA: the song row reads PlayVideoIntent (PlaySongIntent expected). (4) en-US: 'give me music by coldplay' reads PlaySongIntent (PlayArtistSongsIntent expected, an artist-vs-song steal), 'play dinner music' reads PlaySongIntent (PlayMoodMusicIntent expected, a mood steal), plus the 'normal speed' playback-speed row (actual not captured in the mismatch extract). (5) de-DE: 'metti una canzone dei soul coughing'-family rows read no-selection (the JF-684 catalog-gate class on the composed shape; the bare forms route). (6) it-IT: 'chiedi a mia collezione di suonare il brano rapsodia' reads no-selection on profile-nlu even after the 2026-11-24 full retrain; mechanism hypothesis: the armed SongTitle catalog gates intent SELECTION and the wrapper composition pollutes the title span so the ER match fails (JF-684 x wrapper interaction); the JF-945 opener fixed the podcast analog, a 'Di suonare il brano' title-less opener is the candidate fix but each verb-noun combo multiplies rows (the JF-844 each-verb-needs-its-own-row lesson). (7) es-US: 'Reproduce la cancion luciernagas del sur' reads Fallback (free-text MusicRecording slot, no catalog wired there; cause unclear). Verification protocol per row: lab-vs-live compare (same template, catalog delta), then the fix-or-re-pin decision with the maintainer's carrier rule where carriers are involved. This census is the manual input JF-954.5/JF-954.6 will automate.
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
gate-exempt: documentation-only (a census report from live/lab probing; no code, fixtures, templates or models changed); the probing protocol with evergreen controls and dual-probe variance acceptance is documented in the report.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Re-verification complete 2026-11-25, report committed at claudedocs/census_jf959_reverify_2026-11-25.md (602 profile-nlu probes across live and lab, evergreen it+en controls green both passes, 2s spacing). VERDICTS: FIXED (5 rows: the it-IT soul-coughing family already correctly pinned; en-US normal speed now SetPlaybackSpeedIntent; the ja shuffle form green on kana) — STILL-RED-CONFIRMED (5: hi-IN films systematic incl. exact template-sample instantiation; ar-SA song post-rebuild actual flipped PlayVideo->Fallback; the it-IT rapsodia wrapper and es-US luciernagas both PROVEN live-only with identical template content verified, catalog wiring the structural difference) — NEEDS-CARRIER-DECISION (2: hi-IN gana the issue-46 token class live-reproduced; en-US coldplay the missing music-by artist carrier with the identical hallucinated entity in live AND lab) — UNSTABLE (1: en-US dinner music, trainer-marginal). TWO CENSUS FACTS CORRECTED: the 'de-DE no-selection' rows were Italian utterances under a locale mislabel (the JF-684 gating is it-IT-only); es-US is NOT catalog-free (SeriesName/AudiobookTitle/JellyfinArtist wired, get-interaction-model verified). Follow-ups filed: JF-961 (the live-only Fallback pair + the unprobed ja-JP play-form row) and JF-962 (hi-IN systematic + the two carrier decisions + the dinner annotation). The census WAS the deliverable; the task closes as the triage layer, its actions landing in JF-961/JF-962.
<!-- SECTION:FINAL_SUMMARY:END -->

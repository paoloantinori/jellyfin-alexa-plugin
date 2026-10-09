---
id: JF-819
title: >-
  JF-819 - the single-item (song-family) HLS prewrite serve is still UNWINDOWED: a
  single-chapter audiobook resume during a cold encode hits the JF-817 live-edge death
status: Done
assignee: []
created_date: '2026-10-08'
labels:
  - 1.0-blocker
  - bug
  - audiobooks
  - hls
dependencies:
  - JF-778
  - JF-817
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-817 /code-review high gate (finding 1, not covered by any
existing task; JF-818's "informational" note about this serve mischaracterized
a reachable audiobook resume path, amended same-turn).

JF-778 windowed the EPISODE prewrite serve; JF-817 windowed the AUDIOBOOK
CONCAT prewrite rows. The THIRD prewrite family, the single-item (song) serve
(`TryServePrewrittenVideoAudioPlaylist` via `TryServeOwnLiveVideoAudioPrewriteAsync`
and the first-fetch tail), still hands out the FULL un-windowed no-ENDLIST
prewrite, resume-sliced at `?start=`: exactly the JF-817 incident shape.

REACHABLE AUDIOBOOK PATH: `StreamHlsAudiobook` redirects a SINGLE-chapter book
(the one-file audiobook shape, and the JF-794 census found plenty) into
`StreamHlsVideoAudioCore`, threading the resume position (JF-686 made that
work). A deep resume (hours) on a cold cache of an hours-long single-file
book (above the prewrite runtime threshold; the project's own comment says
these encode "for minutes, not seconds") serves the full listing sliced at
the resume segment with no ENDLIST: the player joins at playlist end minus 3x
TARGETDURATION on the un-encoded tail and dies, verbatim the 2026-10-08
20:35 incident on a supported audiobook shape.

WHY NOT FIXED IN JF-817: the fix must ride the shared song-family core, and
TODAY'S SONG BEHAVIOR IS PINNED DELIBERATE: the JF-675 song pin asserts the
full prewrite serves mid-encode (seg_674 of a 45min/4s listing present), and
the JF-680 song twin pins the own-live prewrite row. Windowing the family
flips those pins, i.e. re-decides device-verified behavior for songs
(seconds-long encodes, where the window closes almost immediately and the
cost/benefit differs from books) and for single-chapter audiobooks (minutes,
where the window matters). That re-decision is the maintainer's, not a
drive-by in a blocker fix.

FIX SHAPE: mirror the JF-817 serve (the shared `ComputePrewriteWindow` +
`ResumeInsidePrewriteHonorBand` pair, song-calibrated constants at
SongHlsSegmentSeconds=4) onto `TryServePrewrittenVideoAudioPlaylist`, then
UPDATE the JF-675/JF-680 song pins to the windowed expectations and verify a
short song still serves ~its full listing once the encode completes
(ENDLIST row unchanged). Consider whether the 4s-segment lead should differ
for the single-file-book shape.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no strings or session shapes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: serve-layer HLS fix, no new handler logic; the four new unit pins ARE the deliverable's coverage)
- [x] #8 Locale response strings added to all 17 locales (N/A: no locale strings changed)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Worker round 2026-10-08 (worktree agent-af7b43b92cec35b98 off main tip ab7261f1, the 5521 baseline).

THE DECISION (the filing's scope note, resolved and recorded): the windowed
serve applies to the single-item core UNIVERSALLY (every row through
`TryServePrewrittenVideoAudioPlaylist`: the fast-path warm row, the in-lock
warm row, and the first-fetch tail), NOT gated to the audiobook-redirect
shape. Grounds:
1. The rows are during-encode BY CONSTRUCTION: the warm rows gate on the
   caller's own live art-tick generation (JF-675) and the first-serve row
   runs immediately after the encode is marked. "Window only while an encode
   is live" IS these rows; there is no fresh-play row on this helper to
   preserve. The fresh-play full listing lives on a DIFFERENT row (the
   completed-encode ENDLIST cache-hit serve) and is untouched: the
   `StreamHlsVideoAudio_EncodeCompleted_ServesEndlistPlaylistNotStalePrewritten`
   pin stays green byte-identical.
2. WHY THE PINS PINNED FULL-LISTING (read before deciding, per the scope
   note): the JF-675 song pin's PURPOSE is WHICH listing serves per liveness
   row (prewrite on own-live, ffmpeg ENDLIST on own-dead); its mid-encode
   assertion was the "common case unchanged" control, snapshotting the then
   (2026-09-29) serve shape, the full listing. The JF-680 pin's PURPOSE is
   that the in-lock own-live row serves the prewrite at all (vs the live
   partial); its 2-entry plant is windowing-neutral (floor 2 = the whole
   plant) and needed NO change (verified green unchanged, read count 1 kept).
   Neither pin's intent was the full-listing SPAN; JF-778's 2026-10-05
   device evidence proved that span is itself the death shape mid-encode,
   and the JF-778 round already re-decided the EPISODE twin of the same
   JF-675 mid-encode assertion in place (seg_0001 present, seg_0674 absent).
3. Scoping to the redirect shape only would serve two different shapes for
   byte-identical playlists on identical hardware with no distinguishing
   evidence, and would leave the >= 10min long-song mid-encode fetch (the
   only other runtime band where a prewrite exists) carrying the death.
4. CALIBRATION: the path's own `SongHlsSegmentSeconds`=4 with floor 2 / lead
   3 (the episode-family numbers at the identical segment length; lead 3 x 4s
   = 12s = 3x TARGETDURATION, the device-verified relation; floor 2 = 8s
   keeps the default start at segment 0), as own consts
   `SongPrewriteWindowFloorSegments`/`SongPrewriteWindowLeadSegments` per the
   JF-817 per-family-calibration pattern, plus the
   `SongPrewriteWindowFloor_RespectsSegmentHoldLookahead` constants pin. No
   lead divergence for the single-file-book shape: the lead relation (3x
   TARGETDURATION) is what the device evidence pins, and it is segment-length
   relative, not content relative.

THE FIX: `TryServePrewrittenVideoAudioPlaylist` now reads the prewrite ONCE,
computes the window via the ONE shared pair (`ComputePrewriteWindow` +
`ResumeInsidePrewriteHonorBand`, song-calibrated), truncates the SERVED
listing to the window (the FILE stays full; the reshaped first-fetch pin
asserts the on-disk prewrite still carries all 675 entries), applies the
honor band to `?start=` (outside the band the offset drops with a log), and
threads the content into `ServeVideoAudioPlaylistAsync` as preloaded payload
(the JF-677/JF-680 one-read funnels stay at exactly one read; the JF-680 pin
stayed green on reads.Prewrites()==1). The vanish contract stays
exists-gated/untranslated. NOT copied: the windowing logic is the shared
pair; only calibration consts and logs are new.

RED PROOF (unmodified tree, both TFMs, run before the fix): the incident pin
(one-chapter book via the production `StreamHlsAudiobook` redirect, 45min
chapter, head 22, prewrite aged 200s, `?start=`84s resolving to segment 21
inside the band) failed expected 2 / actual 654 (the full remaining listing
served, seg_674 present); the mid-window pin failed expected 23 / actual
673; the growth pin failed expected 2 / actual 675. Post-fix all green; the
core pin's serve is exactly seg_021..seg_022 with MEDIA-SEQUENCE:21.

PIN RE-DECISIONS (with red-green evidence, not drive-by): the JF-536
first-fetch pin renamed `StreamHlsVideoAudio_CacheMiss_ServesWindowedPrewriteNotLiveEdge`
(the 2-entry windowed serve, the prewrite FILE pinned at 675 entries);
the JF-536 mid-encode pin renamed
`StreamHlsVideoAudio_ActiveEncode_MidEncodeFetchServesGrowingWindowedListing`
(append-only growth 2 -> 6 entries, tail never listed); the JF-675 song
pin's mid-encode control assertion moved to seg_001-present / seg_674-absent
(mirroring the JF-778-updated episode twin; the pin's load-bearing subject,
the own-dead ENDLIST row, untouched). The JF-686 single-chapter pins
(ENDLIST and no-runtime shapes) and the JF-680/JF-681 in-lock pins are
unaffected and stayed green.

GATES. /simplify (4 parallel angles on the final diff): APPLIED the
`PrewriteWindow` doc's consumer count (two -> three families), the ONE READ
contract stated once (doc owns it), the method doc's WHY account compressed
to the song-specific facts with the mechanism referenced to the twins, the
write-site comment trimmed. SKIPPED with reasons, landed in the JF-818
amendment same-turn: the ~30-line orchestration hoist (JF-818 owns it; its
own text predicted the third copy; serve terminators differ, log wording is
test-pinned per family), the fixture segment-plant fold (third copy now
exists; JF-818's test half). /code-review high: NO correctness bug found;
the review independently verified the window arithmetic, floor/hold
relation, honor-band boundary, prefix-walk agreement, vanish/ONE-READ
contracts, pin subjects, and latency hardening. 4 low-severity findings:
F1 APPLIED (the honor-band drop log's outcome sentence made regime-honest on
the song copy; the twins' imprecise copies + the fix-at-hoist note landed in
the JF-818 amendment), F2 APPLIED (the new planter's live partial lists
exactly the planted head), F3 and F4 FILED in the JF-818 amendment (the
per-poll full-Split allocation shape with the review's span-scan
alternative; the third-orchestration-copy confirmation).

VERIFICATION (final tree, both TFMs, never --no-build): touched classes
314/314 after every gate edit (three runs); full suite 5525/5525 net9.0 +
5525/5525 net10.0 (5521 baseline + 4 pins: the incident, mid-window, growth,
and constants pins); `dotnet build Jellyfin.Plugin.AlexaSkill.sln -c
Release` 0 warnings 0 errors (TreatWarningsAsErrors on). No interaction
model, locale, NLU, session-attribute, HttpClient, or config surface touched
(DoD 4/6/7/8 N/A).

FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

GATE-MARKER PROMOTION (2026-10-08, the JF-817 marker F1): promoted into the 1.0 milestone with the blocker label. The marker's judgment: JF-817 is a 1.0-blocker for this same death on the concat path, so shipping 1.0 with the death alive on the single-file book shape (common per the JF-794 census) is the headline bug half-fixed; the 'maintainer must re-decide the pinned JF-675/JF-680 song behavior' argument is a reason to force that decision before release, not to leave the flag off. The scope note stands: the song-family prewrite serves are PINNED full-listing by JF-675/JF-680, so the fix must re-decide those pins deliberately, not drive-by flip them.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-10-08 in worktree agent-af7b43b92cec35b98 (5 commits on the
worktree line, not merged/pushed/deployed): the THIRD prewrite family, the
single-item (song) serve, is now windowed to the encoded region while the
encode runs, closing the JF-817 live-edge death on the OTHER common book
shape (the single-file/one-chapter audiobook the JF-794 census found common,
reached through the `StreamHlsAudiobook` redirect into
`StreamHlsVideoAudioCore`). THE RE-DECISION the gate marker demanded, made
and recorded: UNIVERSAL on the single-item core (all three rows through
`TryServePrewrittenVideoAudioPlaylist`), because those rows are
during-encode by construction (the fresh-play full listing is the
completed-encode ENDLIST row, untouched and pinned green) and the JF-675
and JF-680 pins' intent is which-listing-serves-per-liveness-row, not the
full-listing span (a pre-JF-778 snapshot; JF-778 already re-decided the
episode twin of the same assertion). Calibration: the path's own 4s segments,
floor 2 / lead 3 (12s = 3x TARGETDURATION), own consts + floor-relation pin
per the JF-817 pattern; windowing logic is the ONE shared
`ComputePrewriteWindow` + `ResumeInsidePrewriteHonorBand` pair, not a copy.
RED PROOF on the unmodified tree, both TFMs: the incident pin (redirect
drive, head 22, prewrite aged 200s, resume 84s in-band) failed expected 2 /
actual 654; mid-window 23 / 673; growth 2 / 675; post-fix the incident serve
is exactly seg_021..seg_022, MEDIA-SEQUENCE:21. Pins re-decided WITH
red-green evidence: the two JF-536 pins renamed and reshaped to the windowed
serve (mirroring their JF-778 episode twins; the on-disk prewrite FILE
pinned full at 675 entries), the JF-675 song pin's mid-encode control moved
to the windowed expectation (its own-dead ENDLIST subject untouched); the
JF-680/JF-681/JF-686 pins unaffected and green. Gates: /simplify (4 angles;
4 applied, the orchestration-hoist and fixture-fold skips landed in the
JF-818 amendment same-turn) + /code-review high (no correctness bug; F1 the
drop-log outcome sentence made regime-honest APPLIED with the twins' copies
filed in JF-818, F2 the planter's live-partial honesty APPLIED, F3/F4 filed
in JF-818). Suites: 5525/5525 both TFMs (5521 baseline + 4), touched
classes 314/314 after every gate edit, Release -warnaserror 0/0. Production
surface changed: deploys when merged.
<!-- SECTION:FINAL_SUMMARY:END -->

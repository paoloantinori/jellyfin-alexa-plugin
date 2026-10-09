---
id: JF-820
title: >-
  JF-820 - the audiobook concurrent-encode guard resolves its prewrite by bare request
  ticks: a foreign generation's stale playlist-full can serve under a live encode
status: Done
assignee: []
created_date: '2026-10-08'
labels:
  - bug
  - hls
  - edge-case
dependencies:
  - JF-778
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-817 /code-review high gate (finding 5). PRE-EXISTING row
(the JF-817 windowing neither introduced nor worsened it; it only shrinks
what the row serves), filed because the review surfaced it against JF-817's
new during-encode serve invariant.

The audiobook concurrent-encode guard row gates on BARE registry presence
(`_activeAudiobookEncodes.TryGetValue(parentId, out _)`, any ticks) and then
resolves the prewrite as `GetHlsDirectoryPath(parentId, REQUEST art ticks) +
File.Exists`. When the request's art ticks differ from the live encode's
generation (the art-revert shape: cover refreshed A -> B mid-encode, then the
art read returns A again for a later request; or a library read returning
stale image metadata), the row can find and serve a FOREIGN generation's
leftover `playlist-full.m3u8` (the documented undeletable debris class: the
per-file debris backstop removes stream.m3u8 and segments but not
playlist-full.m3u8). A fully-encoded stale dir defeats the window cap
(head + 1 >= total, windowed = false) and serves the FULL stale listing
whose embedded JF-309 token is expired: every segment fetch it names 401s
for the rest of the encode window.

WHY THE EPISODE FIX DOES NOT TRANSPLANT: the episode prewrite probe's
liveness-aware resolver (JF-775, `ResolveHlsGenerationDirPaths(...,
ownGenerationLiveOrRegistering: true)`) protects the SAME-ticks two-root
shadow (the transient-vs-cache-root stale copy while the caller's own
generation IS live). It does NOT redirect a caller whose own ticks are dead:
the audiobook cross-ticks shape falls to the static root order either way, so
copying the episode probe changes nothing for this defect.

CANDIDATE FIXES (pick deliberately, this is a design decision): (a) resolve
the prewrite through the LIVE REGISTRATION's directory when any generation of
the key runs (serve the running encode's listing regardless of the caller's
art ticks; art only affects the cover/black-frame video track, so the concat
audio timeline is the same); or (b) gate the row on own-ticks liveness and
let a foreign-ticks caller take the lock/503 path instead of serving a
listing whose timeline it cannot verify. Either way, add the missing pin: a
foreign-ticks stale prewrite under a live encode must not serve.
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
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

## Resolution (JF-820, closed 2026-10-09)

DESIGN DECISION (orchestrator, pre-assigned): option (a), resolve the
prewrite through the LIVE REGISTRATION's directory when ANY generation of the
key runs. Art ticks affect only the cover/black-frame video track; the concat
audio timeline is generation-independent, so serving the running encode's
listing to a foreign-ticks caller is correct and refusal (option b) buys
lock-waits and 503s for zero safety gain.

IMPLEMENTATION (VideoAudioController.cs):
- `ActiveEncodeGenerations.TryGetAnyLiveTicks(out long ticks)`: the one gate
  read naming the generation that is actually running.
- `ResolveActiveEncodeHlsDirectory(...)`: the guard's prewrite dir resolution,
  one helper. Own ticks when that generation is live (the byte-identical
  same-ticks row); else the registered generation dir of the live ticks
  (`TryGetRegisteredGenerationHlsDirectory`, written before the mark since
  JF-782) with the deterministic derived-path fallback; else the
  mid-registration window's per-key registration
  (`TryGetRegisteredHlsDirectory`) with the request-ticks fallback. The
  guard's gate STAYS bare presence (own-ticks matching would allow two
  concurrent encodes under different ticks, JF-669); only the directory
  resolution changed.
- No registry-shape change was needed: the entry already carries the live
  slots keyed by ticks, and the directory lives in the cache's generation
  registration map the encode start already writes before the mark.
- Registry consumers audited (TryGetValue/ContainsKey): the guard row (fixed),
  the own-ticks predicate helpers, the debris verdict's tri-state read, the
  seam's EncodeRegistryFor, and the bound-flags ContainsKey row; none read a
  directory off the entry, so the added method is the only surface change.

PER-FAMILY RECORD (work item 2):
- SONG family (`_activeVideoAudioEncodes`) and EPISODE family
  (`_activeEpisodeEncodes`): DIFFERENT shape, no fix needed. Their prewrite
  serves gate on the STRICT own-ticks predicate
  (`OwnTicksGenerationLive`, JF-675): `TryServeOwnLiveVideoAudioPrewriteAsync`
  (song) and the `ServeEpisodeWarmCacheAsync` gate (episode), so a
  foreign-ticks caller never reaches their prewrite rows and cannot serve a
  foreign generation's stale playlist-full. Their residual foreign-ticks
  shapes (cold re-encode under a live foreign generation, the two-root
  shadow) are the JF-775/JF-782 probe+verdict machinery's surface, not this
  defect. The audiobook guard is the ONLY bare-presence prewrite-serve row.

PINS:
- Red proof (unmodified guard, recorded before the fix):
  `StreamHlsAudiobook_ForeignTicksRequestUnderLiveEncode_ServesLiveGenerationListingNotStalePrewrite`
  failed `Assert.Equal() Failure: Expected: 2, Actual: 60`: the guard resolved
  the prewrite by bare request ticks, found the fully-encoded
  foreign-generation stale dir, defeated the window cap (head + 1 >= total),
  and served the FULL 60-entry stale listing (the filed shape).
- Green: the same pin passes serving the live generation's windowed slice
  (seg_0021..seg_0022); counterpart pin
  `StreamHlsAudiobook_OwnTicksLiveUnderForeignStaleDir_ServesOwnWindowedPrewrite`
  locks the resolution priority (own-ticks live beats foreign debris).
- EXISTING PIN REBASED (behavior change, documented): JF-676's
  `StreamHlsAudiobook_UndercountVerdictIsTicksScoped_LiveForeignTicksGenerationDirectorySurvives`
  expected a 503 on the guard fall-through; that 503 is exactly the
  degradation the design decision traded away. The verdict and cleanup asserts
  (ticks-A debris invalidated and cleaned, gen B's live directory surviving)
  are unchanged; the fall-through assert now expects gen B's windowed
  prewrite serve.
- Same-ticks-during-live-encode windowed serve: already pinned by the JF-817
  pins (request ticks 0 = registry ticks 0), not duplicated.

GATES: /simplify + /code-review high on the diff; full suite both TFMs.

## /simplify + /code-review dispositions (2026-10-09)

/simplify (4 angles, converged): APPLIED, the one real finding was test
duplication. `PlantLiveAudiobookEncodeCore` extracted (the shared
dir+segments+prewrite planting core; the JF-817 fixture and the JF-820
foreign-generation fixture both consume it), pin 2's duplicate mock-parent
arrange replaced with local chapter stubs, `TryGetAnyLiveTicks` doc trimmed to
a canonical-pointer at the resolver (the /code-review F5 doc-duplication note
closed the same way). Skipped: none.

/code-review high findings dispositioned:
- APPLIED F6: the live-ticks arm logs a warning when the generation's
  directory registration is missing (the JF-782 register-before-mark
  invariant's break now surfaces instead of degrading silently); the resolver
  became instance for the logger.
- APPLIED (F3, doc): the seam's clear arm is ticks-blind and removes the WHOLE
  key entry; hazard documented on `SetEncodeActiveForTest` (no current caller
  marks two live generations of one key).
- RESIDUAL (F1, documented): the mid-registration arm's request-ticks fallback
  (per-key registration null or its directory vanished) can in principle still
  resolve the caller's dead dir. That window is the transient before the
  marking encode's prewrite exists, no better directory exists to name, and
  the fallback is the pre-JF-820 behavior for that sub-row. Re-open trigger:
  the first observed serve from that arm (a debug-log discriminator can be
  added then).
- RESIDUAL (F2, documented, PRE-EXISTING): the resolution is not atomic
  against generation exit between the gate reads; a caller landing in the gap
  can serve the JUST-FINISHED generation's complete prewrite. That serve shape
  predates JF-820 (the pre-fix code resolved the same dir after the clear) and
  the token is rewritten per serve; not introduced by this change.
- RESIDUAL (F4, documented): the resolution stays a controller-side helper
  rather than a cache API because it composes the controller-PRIVATE
  `ActiveEncodeGenerations` with cache reads; a cache-level move would force
  cross-exposure of the registry type (the /simplify altitude angle's own
  verdict). Consolidation pointer: if the song/episode families ever need "the
  running encode's directory", move the resolution behind ONE shared API then,
  not before.

## Gate-marker tail (2026-10-09, second round)

APPLIED F1 (the multi-live pick): the resolver no longer picks an arbitrary
live generation. `PickLiveGenerationTicks` prefers the caller's own ticks when
live; else the live generation whose registered generation dir IS the per-key
registration's dir (the match is exact: `RegisterHlsDirectoryPath` writes both
maps with the same value, and the segment endpoint's tick-blind resolution
reads the per-key slot, so that generation's listing always names segments the
per-key resolution can find); arbitrary pick only when the comparison cannot
be made (no per-key registration, or no live generation carries a generation
registration); DECLINES (fail closed) when the comparison was made and matched
none. `ActiveEncodeGenerations.GetLiveTicksSnapshot` (under the gate) replaced
the arbitrary-first-slot `TryGetAnyLiveTicks`. PIN:
`StreamHlsAudiobook_TwoLiveGenerations_ServesThePerKeyRegisteredGeneration`
(the seam CAN plant two live generations: two marks at different ticks land as
two slots of one holder; only the CLEAR is whole-key, so F5's seam limitation
does NOT materialize).

APPLIED F2 (the TOCTOU): the resolver is now try-pattern
(`TryResolveLiveEncodeHlsDirectory`); the picked generation's slot is
RE-VERIFIED live before the guard row serves, and a false answer DECLINES the
guard row, falling through to the normal cache/lock rows which serve the
completed ENDLIST cache correctly. PIN RESIDUAL (filed): the exit race itself
is not deterministically plantable, the window sits between two in-method
reads and no test seam can land a clear there (`ProbeLivenessReadForTest`
covers only the probe wrapper, not this resolver); the pin would need a
resolver-side straddle seam. The re-verify is behavior-reviewed, not
pin-backed.

APPLIED F3+F4 (the zero-slot fallback): the request-ticks fallback is GONE.
The mid-registration window (zero slots) now declines the guard row (the
window is the milliseconds between the registry store and the first slot
write, both inside the marking caller's own lock; the accepted cost is that a
foreign-ticks caller in that window can reach the lock path, the pre-JF-820
shape for the window). Comment fixed to the honest statement. PIN:
`StreamHlsAudiobook_RegisteringWindow_FailsClosed_DoesNotServeStalePrewrite`.

APPLIED F6 (token age): a doc line on the resolver: the served listing is
rewritten with the REQUEST's own JF-309 token at serve time, so it inherits
the running encode's token mint; mid-encode secret rotation remains the
shared residual of every token consumer.

REFUTATIONS: none; all six findings were read against the code and stand.

BATTERY (tail): touched classes both TFMs, full counts in the commit message.
<!-- SECTION:NOTES:END -->

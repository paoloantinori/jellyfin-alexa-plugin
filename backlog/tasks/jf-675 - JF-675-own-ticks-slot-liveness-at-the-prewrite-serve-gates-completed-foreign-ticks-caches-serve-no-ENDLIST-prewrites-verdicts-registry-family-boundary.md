---
id: JF-675
title: >-
  JF-675 - own-ticks slot liveness at the prewrite serve gates (completed
  foreign-ticks caches serve no-ENDLIST prewrites) + verdict's registry family
  boundary
status: Done
assignee: []
created_date: '2026-09-29 18:49'
updated_date: '2026-09-29 22:20'
labels:
  - encode-gate
  - playback-speed
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-669 -
    Active-encode-flag-keying-conflates-art-tick-generations-a-live-older-ticks-encode-can-be-declared-debris-after-a-newer-ticks-encode-finishes-first.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn from the JF-669 orchestrator gate-marker round (findings 1 and 2; the scrutiny questions - retry termination, mid-registration window, no masking - all passed).

FINDING 1 (the widened prewrite window): JF-669's any-generation presence semantics widen the prewrite-serve window for a COMPLETED foreign-ticks cache from the monitor-clear lag (seconds) to the sibling encode's entire remaining duration. Scenario: gen A (ticks A, long content) live; gen B (ticks B) completes and writes ENDLIST; every subsequent ticks-B playlist fetch serves dir_B's static no-ENDLIST prewritten listing instead of ffmpeg's ENDLIST playlist for as long as gen A keeps the flag up (pre-JF-669, the flag dropped when B's monitor cleared - the debris bug whose side effect was incidentally the correct serve shape here). Consequence: ExoPlayer reaches the tail of completed content, finds no ENDLIST, and keeps live-edge polling for growth that never comes until gen A finishes. The JF-669 task notes' "identical to today's bare-key behavior: no regression" claim is inexact for this dimension (it holds for the flag-liveness dimension only).

FIX DIRECTION (named by the review): the four prewrite serve gates (song fast/in-lock, episode fast/in-lock, ~460/~489/~782/~818 in the JF-669-era numbering) could first test the CALLER'S OWN ticks slot liveness (ActiveEncodeGenerations exposing slot-liveness) and fall back to any-generation presence only for the conservative directions (the debris verdict, the holds, the guards), so a completed own-ticks encode serves its ENDLIST playlist immediately while cross-ticks liveness still protects directories. Needs its own pin: gen A live, gen B completed, a ticks-B fetch must receive the ENDLIST playlist.

FINDING 2 (latent family-boundary asymmetry, pre-existing): ValidateEpisodeCacheAsync's liveness gate reads only _activeEpisodeEncodes while its _cache.Cleanup(itemId) wipes every {guid}_* directory of the key, which a song-registry encode for the same GUID could be live-writing (StreamHlsVideoAudioCore names directories in the same cache root); TryHoldForNearAheadSegmentAsync already treats liveness as any-of-three-registries, so the asymmetry is real but the routing overlap is theoretical today (handlers route audio to the song path, episodes to the episode path). A doc sentence on the verdict's registry-family boundary belongs with whatever fix lands (or as a standalone doc note if the fix is deferred).

VERIFICATION: the new pin(s) + the JF-669 cross-tick debris pin + the JF-665/668/647 pins all stay green; no same-ticks serve behavior changes (the overwhelmingly common case must stay byte-identical).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DONE 2026-09-29 (worktree agent-a0e192cb9600de5c1, not pushed). Both findings landed:

FINDING 1 (own-ticks slot liveness at the prewrite serve gates): `ActiveEncodeGenerations` gained `IsTickLive(long artModifiedTicks)` (gate-serialized `ContainsKey` on `_slotsByTicks`); a new controller static `OwnTicksGenerationLive(registry, cacheKey, artTicks)` = TryGetValue + IsTickLive (mirrors the MarkEncodeActive -> MarkActive wrapper convention) replaces bare `ContainsKey` at exactly the FOUR prewrite serve gates (song fast/in-lock, episode fast/in-lock; the two first-serve call sites stay ungated: the caller just registered its own slot there). Decision table per gate: own-ticks slot live -> serve the prewrite (byte-identical to the old behavior in every single-generation case); entry present but own-ticks slot dead (a completed own-ticks encode under a live foreign-ticks generation) -> FALL THROUGH to the normal cache serve, so a completed own-ticks encode serves ffmpeg's ENDLIST playlist immediately; no entry -> fall through (unchanged). ZERO conservative-reader changes: `ValidateEpisodeCacheAsync`'s debris verdict, `TryHoldForNearAheadSegmentAsync`'s any-of-three, and the audiobook concurrent-encode guard keep bare any-generation presence (directory protection must not depend on tick matching; the JF-669 cross-tick debris pin depends on it). Residual, documented on IsTickLive: inside the brief mid-registration window the own-ticks probe reads false where bare presence reads true; the gate errs toward ffmpeg's own playlist there (the same file the prewrite-missing fallback serves) - microseconds, no I/O between GetOrAdd and the slot write.

FINDING 2 (registry-family boundary, doc-only): ValidateEpisodeCacheAsync's XML doc gained the REGISTRY-FAMILY BOUNDARY paragraph: the gate reads only the episode registry while the Cleanup it triggers wipes every {guid}_* directory of the key in the shared cache root, so a live same-GUID encode in a sibling registry (the song path names directories in the same root) would not hold the verdict back; TryHoldForNearAheadSegmentAsync reads any-of-three for the same key family; the routing overlap is theoretical today, so the asymmetry is documented with its widening criterion (a real overlap would argue any-of-three at the verdict's gate). No behavior change.

PIN (red-green, both TFMs): `StreamHlsEpisode_CompletedOwnTicksEncode_ServesEndlistWhileForeignTicksGenerationRuns` - the JF-669 pin's construction (real StreamHlsEpisode endpoint, primary-art DateModified mutated between calls, one fake ffmpeg branching on the art-tick suffix of its output dir): gen A (ticks A) parks on a stop file, gen B (ticks B) parks on its own then completes with ENDLIST so its monitor clears only the ticks-B slot (EncodeGenerationCountForTest 2 -> 1), and a ticks-B fetch must receive ffmpeg's ENDLIST playlist (assertion message carries the prewrite-tail marker seg_0674 so the failure shows WHAT was served). A mid-encode fetch BEFORE B's completion pins the unchanged common case (own ticks live: the no-ENDLIST prewrite IS served). RED PROOF: `OwnTicksGenerationLive` toggled to bare `ContainsKey` (the JF-669 shape) -> the pin failed on BOTH TFMs with exactly "a completed own-ticks encode must serve ffmpeg's ENDLIST playlist, not the surviving no-ENDLIST pre-write ... pre-write tail marker seg_0674 present: True"; the mid-encode assertions passed in the red run (the common case is shared by both shapes); toggle reverted, pin green again.

GATES: Skill simplify (4 parallel angles on the final diff). Applied: the comment dedup (simplification S1+S2 - the own-ticks rationale was written six times; OwnTicksGenerationLive's doc is now the canonical account, the four gate comments carry the mechanism lead plus pointers, IsTickLive keeps only the mechanical contract + its two unique facts) and the doc-sync (altitude: both TryServePrewritten* helper docs still said "called on every serve path whose active-encode flag is set"; they now state the JF-675 gate). Justified skips: reuse R1/R2 + simplification S3 (the new pin duplicates the JF-669 pin's fake-ffmpeg script, art fixture, and finally block - second occurrence at the rule-of-three boundary, the JF-669 pin is committed code outside this diff whose construction trail is documented in that task's notes, and the task contract keeps existing pins unchanged; EXTRACT the shared cross-tick helpers when a THIRD cross-tick pin appears - that is the deferral trigger). Efficiency: clean (the steady state returns at the TryGetValue and never touches the holder gate; the encode-window probe is contention-free dictionary ops; the change is net work-REDUCING on the path it fixes - foreign-ticks fetches now skip the File.Exists stat).

Skill code-review (high): 3 findings, none blocking, all dispositioned same-turn. F1 (the own-dead + foreign-live window can serve a KILLED own-ticks encode's stale no-ENDLIST partial playlist, because the debris verdict short-circuits on any-generation presence and cannot re-run - its Cleanup is key-wide) FILED as JF-676 with the review's ticks-scoped validation/cleanup fix direction and a no-band-aid warning (serving the prewrite on no-ENDLIST would re-widen the completed-encode serve JF-675 fixed); F2 (OwnTicksGenerationLive's doc classified the audiobook concurrent-encode guard as a pure directory-protection reader; it is a SERVE/bound decision - serve-or-503 - and own-ticks matching there would newly allow two concurrent encodes of one book) APPLIED as a doc fix; F3 (the episode twin's helper doc said the first-serve call "keeps the bare flag-set shape" - no flag read exists at the first-serve sites, they are ungated by construction) APPLIED as a doc fix on both twins. The review independently verified the mechanism sound: correct gate trio at all four sites, tick-level (not token-level) granularity is the right altitude, the first-serve sites correctly stay ungated, the mid-registration window is negligible, and the pin is deterministic.

ORCHESTRATOR GATE-MARKER ROUND (2026-09-29, on commit 41f7bd08): scrutiny confirmed the change sound (JF-676's mechanism and reachability as filed; no same-ticks serve path changes observable bytes). Three findings applied same-turn in the follow-up review commit. F3 (the real gap): the own-dead fall-through was pinned only on the episode twin; added the song-twin pin StreamHlsVideoAudio_CompletedOwnTicksEncode_ServesEndlistWhileForeignTicksGenerationRuns (real StreamHlsVideoAudio endpoint, same construction, 3-digit song segments + the seg_674 tail marker), with a TWIN-INDEPENDENCE red proof: toggling ONLY the song registry's gate to bare presence failed the song pin on both TFMs while the episode pin stayed green in the same run. F4 (doc drift): MarkEncodeActive's reader enumeration now states the prewrite gates LEFT the presence-reader family in JF-675 (they read IsTickLive and err toward ffmpeg's playlist in the mid-registration window), so the two docs cannot disagree silently. F6 (conventions): every banned parenthetical hyphen in the JF-675-authored lines rewritten (both fast-gate comments, the OwnTicksGenerationLive doc, the boundary doc, both pins' comments). Two same-turn addenda landed in the JF-676 filing: the severity nuance (the pre-JF-675 in-window serve was ALSO broken per the JF-625 tail-404 evidence, so JF-675 is a wash in-window, not a regression) and the stronger sibling finding (ValidateAudiobookCacheAsync fires the key-wide Cleanup with NO liveness short-circuit at all; JF-676's ticks-scoped verdict should cover BOTH validators).

VERIFICATION (final tree, both TFMs, NEVER --no-build): build 0 errors 0 warnings (TreatWarningsAsErrors on); full suite 4756/4756 net9.0 + 4756/4756 net10.0 (4755 baseline + the 1 new pin), exit 0; the encode pin roster re-verified green after every gate edit (JF-665 pair, JF-647 pair, JF-668, JF-669 cross-tick debris, JF-536/JF-531 midencode twins, the new JF-675 pin). No interaction model, locale, NLU, session-attribute, HttpClient, or config surface touched (DoD items 4/6/7/8 N/A: no strings changed, controller-internal serve-gate refinement).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-29 in worktree agent-a0e192cb9600de5c1 (commit on main's worktree line, not pushed): the four prewrite serve gates (song fast/in-lock, episode fast/in-lock) now test the CALLER'S OWN art-tick slot via ActiveEncodeGenerations.IsTickLive (gate-serialized slot probe) + the OwnTicksGenerationLive controller helper, so a completed own-ticks encode serves ffmpeg's ENDLIST playlist immediately instead of its surviving no-ENDLIST pre-write for a foreign-ticks generation's whole remaining duration (the JF-669-review finding 1). The conservative readers keep bare any-generation presence unchanged (debris verdict, near-ahead hold, audiobook serve/503 guard - each with its own honest reason now stated on OwnTicksGenerationLive, the canonical doc). Finding 2 landed as the REGISTRY-FAMILY BOUNDARY doc on ValidateEpisodeCacheAsync (episode-registry-only read vs key-wide Cleanup wipe, any-of-three widening criterion, no behavior change). Pin: StreamHlsEpisode_CompletedOwnTicksEncode_ServesEndlistWhileForeignTicksGenerationRuns (real endpoint, JF-669 construction, plus the unchanged-common-case mid-encode fetch); red proof: the helper toggled to bare ContainsKey failed both TFMs showing the pre-write served (seg_0674 marker True). Gates: Skill simplify 4-angle (2 applied: comment dedup with the canonical account on OwnTicksGenerationLive, IsTickLive shrunk to its unique facts; skips: the pin's second-occurrence duplication with the JF-669 pin, extraction deferred to the third cross-tick pin) + Skill code-review high (3 findings: F1 filed same-turn as JF-676 - the killed-own-ticks residual window needing a ticks-scoped debris verdict; F2+F3 doc-accuracy, applied). Suites: 4756/4756 both TFMs (4755 baseline + 1 pin), encode pin roster 9/9 green after every gate edit. Residual JF-676: the own-dead + foreign-live window serves a killed own-ticks encode's stale partial playlist until the foreign generation exits; needs ticks-scoped validation, do not band-aid with a no-ENDLIST prewrite re-serve.
<!-- SECTION:FINAL_SUMMARY:END -->

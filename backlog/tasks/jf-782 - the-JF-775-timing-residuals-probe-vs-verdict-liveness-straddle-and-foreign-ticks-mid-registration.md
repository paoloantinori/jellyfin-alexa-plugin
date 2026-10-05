---
id: JF-782
title: >-
  JF-782 - the JF-775 dir-authority residuals: the probe-vs-verdict liveness
  straddle (a mark landing between the two reads serves the cache-root shadow
  the deleted JF-774 F3 redirect used to cover), the foreign-ticks
  mid-registration sub-window, and the full-slot foreign-registration
  overwrite (pre-existing JF-774 containment boundary)
status: To Do
assignee: []
created_date: '2026-10-05'
labels:
  - streaming
  - cache
  - tech-debt
dependencies:
  - JF-775
priority: low
---

## Description

Filed 2026-10-05: legs 1 and 2 from the JF-775 /simplify round (the altitude
angle's finding 2, with the efficiency angle's F1 caveat converging on the
same seam), leg 3 from the JF-775 /code-review high round (its finding 2,
which surfaced a PRE-EXISTING gap rather than a regression); filed per the
same-turn landing rule rather than silently shipping the narrowed or
mis-documented protection.

JF-775 consolidated the dir authority into the liveness-aware probe: the
ordered probe consumes the caller's OwnTicksGenerationLiveOrRegistering read
and returns ONLY the registered dir's file while that read is true and the
registration is contained, which closed the JF-774 gate-marker addendum's
mid-registration residual. The consolidation moved the authority decision to
PROBE time, and two timing residuals now remain open:

1. THE PROBE-VS-VERDICT STRADDLE. The probe reads liveness at probe time; the
   paired debris verdict re-reads at verdict time (after the probe's file
   I/O, a microsecond-to-millisecond gap). A generation MARKED in that gap is
   unseen by the probe (which resolved the static order, first hit possibly
   the undeletable cache-root shadow) but accepted unread by the verdict (its
   loose read now true, Content null), and the serve row serves the verdict's
   first-hit file: the shadow's stale bytes. The deleted JF-774 F3 redirect
   re-derived authority at SERVE time and covered exactly this straddle on
   the own-live row (it would serve the registered dir's stream.m3u8, absent
   pre-ffmpeg, whose read vanishes and falls the request through to the
   lock-scope concurrent-serve hold). Conjunction: the undeletable same-key
   cache-root shadow + an oversize (transient-root) encode whose mark lands
   in the probe-to-verdict gap of a replay + the prewrite not yet written
   (it lands after process start). Narrower than the mid-registration window
   JF-775 closed, same blast radius when hit (the shadow's expired JF-309
   token 401s every segment fetch for the whole encode window).

2. THE FOREIGN-TICKS MID-REGISTRATION SUB-WINDOW. In the zero-slot window the
   incoming registration may be for FOREIGN ticks (an art change mid-flight):
   the resolver's containment correctly ignores the foreign registration
   (static order; the shadow is probed) and the registering verdict accepts
   it unread, so the shadow serves. No resolver shape can close this (the
   t1 answer for a dead t1 generation IS the static order); only the VERDICT
   can, by narrowing its 0-slot acceptance to registrations provably for the
   caller's ticks, which is only sound if EVERY encode path registers its
   resolved dir BEFORE marking (the episode path does since JF-774's review
   F1; the song, variants, and audiobook paths still register after their
   first-segment wait, sound today only via single-rootedness, the
   precondition now documented on VideoAudioCache.ResolveHlsGenerationDirPaths).

3. THE FULL-SLOT FOREIGN-REGISTRATION OVERWRITE (pre-existing, surfaced by
   the JF-775 code-review round). The per-key registration slot
   (_hlsDirLookup) is shared by every generation of the key, so a concurrent
   FOREIGN-ticks encode's RegisterHlsDirectoryPath overwrites it while the
   caller's OWN-ticks generation is still fully live: the resolver's
   containment correctly refuses the foreign dir, the static order answers,
   and an undeletable other-root shadow can win the liveness-accepted row.
   This is NOT a JF-775 regression (the deleted JF-774 F3 redirect had the
   same containment refusal by design, "a foreign-ticks registration can
   never redirect the serve"); it is filed here because the JF-775 wrapper
   doc's "never the other root's stale shadow" claim needed an honest scope
   for it, and the deep fix is the same family as legs 1 and 2: the
   registration must become generation-scoped (keyed (key, ticks), or the
   ActiveEncodeGenerations slot carrying its dir), so a foreign overwrite
   cannot mask the own generation's registered dir. Conjunction: the
   undeletable same-key shadow + an art change starting a foreign-ticks
   encode mid-watch + a replay of the original ticks.

FIX SHAPES (for whoever picks this up):
- Straddle: the single-snapshot shape is the deep fix: thread the probe's
  liveness answer into the verdict so ONE evaluation decides both (when the
  probe's read was false but the verdict's is true, the verdict must take the
  READ row and judge the hit's content instead of accepting unread; for the
  shadow that is the no-ENDLIST debris row, whose failed undeletable cleanup
  falls through to the transient leg). The shallow fix is un-collapsing
  (re-deriving authority at serve time), which re-adds the duplication JF-775
  removed; prefer the snapshot.
- Foreign-ticks window: adopt register-before-mark on the remaining three
  encode paths (the one-wrapper idiom), then narrow the verdict's 0-slot arm
  by registration containment.
- Foreign-overwrite: generation-scope the registration (a (key, ticks)
  lookup, or the live slot carrying its dir), keeping the tick-blind
  FindHlsDirectory leg on the per-key map it needs for post-restart segment
  resolution.
- DISCRIMINATING PIN PREREQUISITE: legs 1 and 2 need a seam that fires
  between the probe's liveness read and the verdict's (the existing
  PlaylistContentReadForTest observer fires after both), e.g. a
  ProbeLivenessReadForTest hook on the GetCachedHlsPlaylistLiveAwareAsync
  wrapper, or the verdict-threading parameter itself once shaped; leg 3 needs
  no new seam (a planted foreign registration plus the shadow plants the
  state directly).

VERIFICATION BAR: one pin per leg, red on the pre-fix tree (the straddle pin
plants the mark inside the gap via the seam; the foreign-ticks pin plants a
foreign-ticks registering entry plus the shadow; the overwrite pin plants a
foreign registration over a live own-ticks slot plus the shadow), all
asserting the shadow's marker is absent from the served bytes.

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

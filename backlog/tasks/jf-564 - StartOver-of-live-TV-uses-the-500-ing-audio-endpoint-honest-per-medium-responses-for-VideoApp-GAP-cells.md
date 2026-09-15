---
id: JF-564
title: >-
  StartOver of live TV uses the 500-ing audio endpoint; honest per-medium
  responses for VideoApp GAP cells
status: In Progress
assignee: []
created_date: '2026-09-14 20:08'
updated_date: '2026-09-15 05:54'
labels:
  - bug
  - live-tv
  - transport
  - ux
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Audit 2026-09-14 (matrix intent x medium, code-verified): StartOverIntentHandler (~lines 126-140) relaunches Movie/Episode via VideoApp (coherent) but LiveTvChannel falls to the audio branch /Audio/{id}/stream — the endpoint the docs and PlayChannelIntentHandler say 500s for live sources (live TV must launch via VideoApp through ILiveTvStreamResolver). FIX: StartOver of a LiveTvChannel routes through the live-TV resolver / VideoApp launch (a "restart" of live TV = rejoin the live stream; consider speaking the distinction). Related task in the same audit: Next/Previous during VIDEO and TV emit silent Empty() responses (items are not in NowPlayingQueue; a stale queue can even emit an AudioPlayer.Play mid-video that VideoApp cannot honor), and Pause/Cancel during VideoApp/LiveTV/audiobook sends a no-op AudioPlayer.Stop while still ending/opening a session. UX decision to make (Paolo): keep silent no-ops, or speak one honest per-medium line (e.g. "usa il pulsante indietro per chiudere il video" / "il live va avanti da solo"). Scope: StartOver-LiveTV routing (code fix) + the honest-response decision and implementation for the VideoApp GAP cells (Next/Previous/Pause/Cancel during V/TV/AB). Audit cells: StartOverIntent TV=GAP, NextIntent V/TV=GAP, PreviousIntent V/TV=GAP, PauseIntent V/TV/AB=GAP, CancelIntent V/TV/AB=GAP.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes (3826/3826 both TFMs)
- [x] #3 No new compiler warnings introduced (Release -warnaserror clean)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (no session attributes added)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (no HttpClient use added)
- [x] #6 NLU test fixtures updated if interaction model changed (no model change; strings are handler-side)
- [x] #7 E2E test added for new intent or handler logic (handler logic covered by 20 new unit tests: StartOver live-TV x3, GAP cells x17 in VideoAppGapHonestResponseTests; no new intent, E2E fixture matrix unchanged)
- [x] #8 Locale response strings added to all 17 locales (CannotNavigateVideoByVoice, CannotNavigateLiveTvByVoice, CannotPauseVideoByVoice; validate_locales PASS)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked) (0 P1, 0 P2; P3-1/JF-568 + P3-2/JF-569 filed, P3-3 fixed same turn)
<!-- DOD:END -->

## Implementation notes (2026-09-15, complete, uncommitted)

- UX DECISION (recorded in code): honest per-medium lines replace the silent no-ops.
  Three strings, one per family: CannotNavigateVideoByVoice (Next/Prev during
  Movie/Episode), CannotNavigateLiveTvByVoice (Next/Prev during live TV),
  CannotPauseVideoByVoice (Pause AND Cancel during video/live TV/VideoApp audiobook;
  mentions the touchscreen AND the back button per the Stop/Session reference's
  documented workaround). STOP keeps the docs-mandated silent AudioPlayer.Stop +
  session-end shape.
- Medium detection: BaseHandler.PlayingMedium + ResolvePlayingMedium (ledger-first via
  the JF-563 device last-played ledger + StreamTokenCodec token decode; consumes
  IsVideoAppLaunchItem per the JF-505 no-hand-written-type-list rule). Token names the
  ledger item = Audio whatever the kind (movie audio-transcode, flat-audio book: the
  DB read is even skipped); empty ledger/unresolvable item = Unknown, and EVERY
  consumer keeps pre-JF-564 behavior on Unknown (cold-handler music semantics pinned
  by tests).
- Stale-queue guard: BuildVideoAppTransportRefusal (shared Next/Previous entry helper)
  - during ANY VideoApp-family medium the AudioPlayer directive is never emitted
  (video/live TV answer the honest line, a VideoApp book keeps the silent Empty); the
  Next-during-video test is the mutation pin (old code played the stale queue's next
  SONG mid-video).
- Pause/Cancel honest response = AudioPlayer.Stop directive kept (audio-stop
  invariant) + session-ending speech (IntentRequest, so JF-299 event rules do not
  apply); PauseKeepsSession does not apply to the VideoApp cells.
- StartOver live TV: rides BuildChannelLaunchResponseAsync (the JF-483 shared launch
  block, third consumer), gated on LiveTvEnabled like PlayChannel, branch above the
  progress clear (a live source has no resume position); resolver null = the same
  MediaTypeNotAvailable tell PlayChannel speaks.
- Gates: /simplify 4-reviewer pass (classifier consumes IsVideoAppLaunchItem, guard
  twin extracted, ledger-first DB-read skip, test fixture dedup into TestHelpers) +
  code-review high dedicated reviewer (0 P1, 0 P2; P3-3 fixed, P3-1 -> JF-568,
  P3-2 -> JF-569). Suite 3826/3826 both TFMs, Release -warnaserror 0 warnings.


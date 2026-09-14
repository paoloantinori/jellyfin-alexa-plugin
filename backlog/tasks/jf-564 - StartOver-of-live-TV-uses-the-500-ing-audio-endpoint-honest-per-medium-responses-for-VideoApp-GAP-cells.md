---
id: JF-564
title: >-
  StartOver of live TV uses the 500-ing audio endpoint; honest per-medium
  responses for VideoApp GAP cells
status: To Do
assignee: []
created_date: '2026-09-14 20:08'
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

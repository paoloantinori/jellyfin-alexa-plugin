---
id: JF-304
title: >-
  Mid-track "avanti" (next) intermittently not routed to skill — investigate
  eager prefetch
status: In Progress
assignee:
  - claude
created_date: '2026-07-02 20:11'
updated_date: '2026-07-16 20:23'
labels:
  - bug
  - playback
  - audio-player
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/10'
  - JF-301
  - JF-302
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Split out of JF-302 (which is now stop/cancel-only). "Next" and "stop" are DIFFERENT issues: "next" demonstrably reaches the skill (at least one success on-device), "stop" never does (skill competition). They must not share a verdict.

SYMPTOM: During AudioPlayer playback, saying "avanti" (next) MID-TRACK sometimes makes the Echo speak its own "can't move to the next track" instead of advancing — and the plugin receives nothing.

EVIDENCE (on-device, 2026-07-02, session 76e19023):
- 17:39 first "avanti" → plugin SERVED it: played "Lifeless Dead" (0d16d64e), confirmed by persisted DeviceQueue lastPlayedItemId + shuffle reorder (index 3→1). So AMAZON.NextIntent DID reach the skill this time.
- Later "avanti" (post shuffle-off) → plugin received ZERO NextIntent/PlaybackController events. The "can't move to the next track" voice was the ECHO's own — the plugin's NextIntentHandler only returns silent Empty(), never speech, so that message could not have come from us.

HYPOTHESIS (the difference from stop): PlaybackNearlyFinishedEventHandler enqueues only ONE track ahead, and only near end-of-track. A mid-track "avanti" hits an empty Echo next-track buffer, so the Echo handles "next" locally and speaks "can't move" instead of routing NextIntent to the skill. Near end-of-track (buffer populated), "avanti" routes to the skill and works — matching the intermittent success.

This is NOT skill competition: proven by the 17:39 success (NextIntent reached the skill). Stop (JF-302) IS skill competition (simulator <IntentForDifferentSkill>), which is why stop never succeeds.

CANDIDATE FIX: prefetch eagerly — enqueue the next track at PlaybackStarted (not only at PlaybackNearlyFinished), or keep 2+ tracks buffered — so the Echo always has a next track and is more likely to route mid-track "avanti" to the skill.

UNCERTAINTY (diagnostic gate first): even with a populated buffer, it is unproven whether mid-track "avanti" reliably routes to the skill vs. the Echo playing the buffered track locally or handing off to the default music service. Must be verified on-device before committing to the prefetch change.

Do NOT conflate with JF-302 (stop). That one is a documented platform limitation with no code fix; this one has a plausible plugin-side mitigation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 DIAGNOSTIC GATE: with current code, capture at what offset PlaybackNearlyFinished fires and whether a mid-track 'avanti' (before it fires) ever routes NextIntent to the skill. Record the offset + verdict in notes.
- [ ] #2 DECISION recorded: eager-prefetch vs document-and-accept, with rationale, after the diagnostic gate.
- [ ] #3 If implementing eager prefetch: a mid-track 'avanti' (≥3 invocations across different points in a track) is served by the skill (NextIntent received + next track played) instead of the Echo speaking 'can't move'.
- [ ] #4 No regression to gapless auto-advance (PlaybackNearlyFinished path) or to shuffle (JF-301) next-track resolution.
- [ ] #5 On-device verification with debug logs showing the skill receiving and serving the mid-track next request.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
PLAN (2026-07-02):

STEP 1 — CODE ANALYSIS (DONE):
- Prefetch is EXCLUSIVELY in PlaybackNearlyFinishedEventHandler.HandleAsync: it issues exactly one AudioPlayer.Play(Enqueue) for ResolveNextItemId(...), and ONLY when the Echo reports the current track near-done. No other path enqueues.
- PlaybackStartedEventHandler does NO prefetch — it records PlaybackStartInfo + returns a keep-alive ack (JF-299).
- => Mid-track (before NearlyFinished fires) the Echo's AudioPlayer buffer holds ONLY the current track. A mid-track "avanti" finds no next track => the Echo handles "next" locally and speaks "can't move" instead of routing AMAZON.NextIntent to the skill. Matches the intermittent symptom (works near end-of-track when a track IS buffered).

STEP 2 — EAGER-PREFETCH CANDIDATE (the fix):
- Enqueue the next track in PlaybackStartedEventHandler so the buffer is populated from track start. Mechanically allowed: a Play directive IS permitted from event handlers (JF-299 only forbids shouldEndSession=false, not Play).
- RISK (medium-high): must avoid DOUBLE-enqueue (NearlyFinished would otherwise re-enqueue the same next item => duplicate track / skip). Must preserve gapless auto-advance, progressive queue continuation (TryFetchContinuationBatch), radio mode, PostPlay AutoPlay, and JF-301 shuffle — all currently intertwined with the NearlyFinished enqueue. Likely needs a "last enqueued token" guard per device and careful split of responsibilities between PlaybackStarted (enqueue next) and PlaybackNearlyFinished (continuation/radio/PostPlay only, skip if already prefetched).

STEP 3 — DECISIVE ON-DEVICE DIAGNOSTIC (RUN FIRST, ~2 min, no code change — logging already present):
The fix is only worth building if a populated buffer actually makes the Echo route mid-track "avanti" to the skill. Test on device with debug logging on:
1. Play a track. Note the `PlaybackNearlyFinished: ... offset=NNms` log => records the prefetch offset (answers AC#1).
2. BEFORE that offset (buffer empty): say "avanti". Hypothesis: Echo speaks "can't move", NO `NextIntent: entered` log.
3. AFTER that offset (buffer has next track): say "avanti". 
   - If `NextIntent: entered` appears and next track plays => routing works when buffered => BUILD eager prefetch.
   - If still no NextIntent / Echo "can't move" => skill competition (like stop/JF-302) => document-and-accept, do NOT build prefetch.

DECISION RULE: do not code Step 2 until Step 3 verdict is in. Verify-don't-assume: the entire fix rests on the unverified premise that a buffered track changes routing.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
CODE ANALYSIS 2026-07-02: Confirmed via PlaybackNearlyFinishedEventHandler.cs + PlaybackStartedEventHandler.cs. Prefetch = one Play(Enqueue) at NearlyFinished only; PlaybackStarted does no enqueue. Double-enqueue risk is the main implementation hazard if we add eager prefetch at PlaybackStarted. NextIntentHandler already serves the next item correctly WHEN routing reaches it (proven by today's 17:39 success) — so the plugin side is fine; the only lever is keeping the Echo buffer non-empty mid-track. No code change made: gated on the on-device diagnostic (plan Step 3).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-16 20:23
---
2026-07-16 reconciliation: confirmed this is DISTINCT from JF-302 (stop). JF-302 = stop/cancel (documented platform limitation, no code fix); THIS task = mid-track next/avanti, with a plausible plugin-side fix (eager prefetch at PlaybackStarted). Deliberately split out — do not re-conflate. Status: still genuinely OPEN, gated on the on-device diagnostic (Plan Step 3) which has not been run. Left In Progress; not superseded, not done.
---
<!-- COMMENTS:END -->

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

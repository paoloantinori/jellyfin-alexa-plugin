---
id: JF-808
title: >-
  JF-808 - the playlist play path (ask and confirm) carries no Layer-1 warming gate
status: Done
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - playback
dependencies:
  - JF-806
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPlaylistIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-806 round (2026-10-07, same-turn per the
review-recommendation rule; the /code-review high round's finding 1).

JF-806 gated the collection-fetch confirm legs (book, MusicAlbum, artist)
on the warming axis, and JF-807 tracks the book ASK's missing gate, but the
PLAYLIST play path is warming-ungated end to end: PlayPlaylistIntentHandler
and ShufflePlayIntentHandler carry no GuardIndexReady call (absent from
WarmingGateCoverageTests.ExpectedGatedHandlers), and the YesIntent
playlist confirm arm (YesIntentHandler.PlayPlaylist) runs a recursive
unpaged MediaTypes=Audio GetItemList over the playlist folder with no gate
either. During the post-restart index-load window a playlist request (ask
or confirm on an open prompt) hits the cold database inside Alexa's ~8s
window and surfaces as on-device INVALID_RESPONSE: the JF-419 live-incident
class. JF-806 deliberately rescoped the playlist leg out of the MUSIC
axis (its ask does not gate MusicEnabled and playlists are cross-type
always-allowed, so gating only the confirm would CREATE the divergence);
the WARMING axis is a different axis and applies to BOTH sides.

FIX SHAPE: the JF-807 pattern (the PlayAlbumIntentHandler coarse
precedent): decide whether the playlist ask warrants the Layer-1
GuardIndexReady(_artistIndex) entry gate (it needs the IArtistIndex ctor
param threaded into both playlist handlers) and gate the confirm arm to
match whatever the ask answers; add any gated handler to
WarmingGateCoverageTests.ExpectedGatedHandlers; decide the
warming+cross-type interaction (playlists are always-allowed on the
content axis, but the warming axis is about cold-database cost, not
permissions). Mind the confirm-must-match-ask rule when choosing the
arm's ordering.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release -warnaserror: 0 warnings 0 errors)
- [x] #2 dotnet test passes (full suite 5466/5466 BOTH TFMs at the final state; baseline 5458 + the 8 new pins)
- [x] #3 No new compiler warnings introduced (Release -warnaserror clean, both TFMs compile clean)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute changes; the warming refusal is a thrown SkillWarmingUpException answered by RequestPipeline, no attributes ride it)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes; the gate is a static readiness read)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction-model change; SkillWarmingUp already existed in all 17 locales)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new intent; handler logic covered by the 8 unit pins incl. red proofs, both TFMs)
- [x] #8 Locale response strings added to all 17 locales (N/A: no speech changes; the SkillWarmingUp Tell is the pre-existing string)
- [x] #9 /simplify passed (no blocking cleanups remaining) (4-agent round: 6 findings applied incl. the SetupPlaylist hoist to TestHelpers; 1 skip recorded, the unobservable ToList defensive copy)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked) (4 findings, all 4 applied same-turn: the CreateRequest positional-arg defect, the shuffle ready-path pin, the confirm-driver AllText assert, the shuffle elicit-hatch twin)
<!-- DOD:END -->

## Final Summary

Closed 2026-10-07, worktree branch (4 commits: red scaffolding, gates,
simplify round, code-review round; not merged, per the task mandate).

THE GAP, CLOSED: the playlist play path is no longer warming-ungated end
to end. GuardIndexReady(_artistIndex) now stands at all three sites:

1. PlayPlaylistIntentHandler (the ask), after the empty-slot elicit and
   the cancel hatch, before the shared builder's first cold query.
2. ShufflePlayIntentHandler (the shuffle twin), identical placement; read
   from source, it shares AlbumPlayService.BuildPlaylistPlayResponseAsync
   and its whole cold surface verbatim (only the shuffle flag differs), so
   nothing on it was left ungated.
3. YesIntentHandler.PlayPlaylist (the confirm arm), inside the method at
   entry (the JF-806 F3-inside-the-method shape, so a future direct caller
   cannot re-open the axis), before the recursive unpaged
   MediaTypes=Audio whole-track fetch.

INDEX-CHOICE DECISION (the JF-807 rule applied): playlists have no
in-memory index of their own (neither the artist nor the song n-gram index
serves Playlist items), so the gate is a stand-in on the artist index, the
family's established stand-in (the PlayAlbum precedent, joined by books in
JF-807); the album ask, the sibling the playlist path most resembles,
gates the same index, and the confirm arm matches the asks on it so ask
and confirm answer identically in the warming window. The choice adds no
new rule, so it is recorded on the handler gate comments (with the pointer
to IndexWarmingGate's stand-in rule); IndexWarmingGate's doc got only the
family-list update, plus repair of the mangled mid-sentence F3 insert it
already carried on main, which also removed its stale "the playlist gap is
JF-808" marker.

PLACEMENT FACTS: no PlaylistsEnabled-like flag exists (verified in
PluginConfiguration; playlists are cross-type always-allowed per the
JF-806 decision), so there is no flag-gate ordering to mirror and the
intersection pin is honestly N/A (recorded in the test docs and the
switch-arm comment, which was rewritten from "carries NEITHER gate" to
the new state: no music gate by design, warming gated since JF-808). The
playlist path sends NO pre-query announcement, so the F5 ready-side
announcement pin has nothing to observe; the empty half of the contract
(no progressive before the refusal) is pinned on the ask AND, after
review F3, on the shared confirm-leg driver.

RED-PROOF EVIDENCE (pre-gate tree, scaffolding disclosed as ctor params +
test doubles only, NO gates): both TFMs, the three warming pins failed
with "Assert.Throws() Failure: No exception was thrown" (net9.0
Failed:3/Passed:62, net10.0 Failed:3/Passed:62), the clean no-throw shape
(the ungated path ran and completed; mocks served so no mock-default
crash), companion pins green pre-fix by design. Post-fix all flip green.

ROSTER: PlayPlaylistIntentHandler and ShufflePlayIntentHandler added to
WarmingGateCoverageTests.ExpectedGatedHandlers; the IL scan is green in
both directions.

VERIFICATION: touched classes 73/73 on both TFMs at the final state; full
suite 5466/5466 net9.0 AND net10.0 (baseline 5458 + 8 new pins); Release
-warnaserror build 0 warnings 0 errors. GATES: /simplify (4 parallel
agents: 6 applied incl. the SetupPlaylist hoist to TestHelpers making the
resolvable-playlist fixture one-owner, 1 skip recorded) and /code-review
high (4 findings, all applied: the CreateRequest positional-arg defect in
the shuffle pin, the shuffle ready-path transparency pin, the
confirm-driver no-progressive assert, the shuffle elicit-hatch twin).
pa:reflect verdict ALIGNED.

INCIDENT, HANDLED HONESTLY: the FIRST full net9.0 run at the pre-review
final state had one unrelated VideoAudioControllerTests teardown failure
(the JF-731 Dispose backstop). Matrix: isolation 174/174 green, re-run
green 5464/5464, net10.0 green, base tree 1ea40cd4 green 5458/5458;
filed same-turn as JF-810 with the matrix and no root-cause claim.

TEST INFRASTRUCTURE NOTE: the confirm arm's ((Folder)playlist).GetItemList
runs real recursive-folder machinery needing server-injected statics
(BaseItem.ConfigurationManager) the test host does not stub, so the pins
drive a TestHelpers.TestItemsFolder double whose GetItemsInternal override
bypasses the machinery; the resolvable-playlist fixture lives in
TestHelpers.SetupPlaylist (hoisted from the shuffle suite's private copy).

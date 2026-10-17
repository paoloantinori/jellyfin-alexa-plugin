---
id: JF-329
title: 'Feature: Photo slideshow on Echo Show (APL)'
status: In Progress
assignee: []
created_date: '2026-07-12 15:01'
updated_date: '2026-10-12 20:20'
labels:
  - feature
  - apl
  - photos
  - 'gate:device-round'
milestone: m-10
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Apl/
  - Jellyfin.Plugin.AlexaSkill/Alexa/Interface/
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
There is no `Photo` handling anywhere in the plugin (functional review 2026-07-12). Jellyfin supports photo libraries and the Echo Show is a natural surface. "Show my {album} photos" → an APL image sequence/slideshow. Niche but differentiating and visual, and it avoids the AudioPlayer platform limits entirely (pure APL, no default-music-service competition).

New intent: resolve a Jellyfin photo album/library folder for the linked user (respect library gating), build an APL slideshow directive cycling images via the existing AplHelper patterns, and handle Echo Show capability detection (fall back to a spoken "this needs a screen" on audio-only devices). Handler + IntentNames + samples (17 locales, it-IT YAML) + response strings + tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 User can request a photo album/library by name and see an APL slideshow on an Echo Show
- [ ] #2 Photo selection respects per-user library gating
- [ ] #3 On audio-only devices the skill responds gracefully that a screen is required (capability detection)
- [ ] #4 Slideshow uses existing AplHelper patterns and valid APL (passes validate_apl.py)
- [ ] #5 Samples + response strings across all 17 locales; unit tests included
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DEVICE PROBE LIST + CLOSURE PLAN (2026-10-11 00:50, orchestrator; merged after full verification: validators/locales/versions/APL PASS, 5677/5677 both TFMs, 87/87 targeted pins): the task closes when the SHOW probes pass. 1) Echo Show it-IT one-shot 'chiedi a mia collezione mostra le foto di <album>': slideshow renders, cycles every 8s, SURVIVES PAST ~30s (the TimeoutType=MAX probe), swipe wraps. 2) THE TWO UNPROVEN APL CONSTRUCTS, PROBE FIRST (the review's F2): handleTick continuing after the session-ending Tell, and the pager.currentPage self-reference inside SetPage - if the screen freezes on photo 1 while the speech announces, it is one of these; the fallback designs are AutoPages or a session-keep-alive shape (the web APL simulator can derisk this before the device). 3) Audio-only Echo: 'show my photos' speaks PhotosRequireScreen, no INVALID_RESPONSE. 4) Empty-slot 'mostra le mie foto' on the Show (the recent-photos default). 5) Post-deploy routing: the steal pins (mostrami le mie foto / show me my photos must NOT go to PlayVideo - the orchestrator runs profile-nlu after the rebuild). Follow-ups filed: JF-862 (the resolve-chain convergence at the third caller), JF-863 (the AudioPlayer interceptor's blanket refusal vs non-audio intents).

ROUTING VERIFIED LIVE (2026-10-11 01:00, orchestrator; deploy 92eb4c80 + 17/17 rebuild SUCCEEDED): 'mostra le foto di vacanze' -> ShowPhotoAlbumIntent 4/4; 'show photos of rome' -> ShowPhotoAlbumIntent 4/4; BOTH steal guards hold ('mostrami le mie foto' and 'show me my photos' -> ShowPhotoAlbumIntent 4/4, NOT PlayVideo). Probe-list item 5 CLOSED. Remaining for closure: the Show rendering probes (items 1-2, incl. the two unproven APL constructs) and the audio-only fallback (item 3) - the maintainer's device round; the web APL simulator can derisk item 2 first.

ITEM 2 DERISKED, CONSTRUCT (b) VERDICT: REFUTED AS SHIPPED (2026-10-12, web-sim probe). Environment: Amazon's official docs Code Sandbox (the live no-login APL runtime embedded in developer.amazon.com doc pages, driven via local Chrome; no login, no account state touched; sandbox edits are client-side ephemeral by design). The EXACT SlideshowTemplate document (AplHelper.cs lines 863-918, incl. the Back extension and TimeoutType MAX datasource shape) was pasted verbatim with 5 test images: the pager sat FROZEN on page 0 for the full 28s sample window. Four independent, each-fatal causes, all verified against the live docs + runtime: (1) SetPage's `value` is REQUIRED (apl-set-page-command.html property table: value Integer REQUIRED, "must have a value for the command to run") and the shipped command omits it; (2) `position` is the enum relative|absolute, the shipped command binds an integer expression there; (3) the self-reference does not resolve AT ALL: ${slideshowPager.currentPage} renders EMPTY in data-binding (control render: cp=[] ip=[] op=[] N=5; even ${slideTitle.opacity} and ${slideshowPager.initialPage} render empty) because current Pager docs expose the page ONLY as event.source.page inside event handlers, never as a readable component property in ordinary expressions; (4) structural: tick commands ALWAYS run in fast mode (apl-tick-event-handlers.html: "Tick commands always run in fast mode") and SetPage's documented fast-mode behavior is "Ignored" (apl-commands.html fast-mode table); only an explicit `sequencer` property promotes a tick command to normal mode. So the predicted failure mode ("screen freezes on photo 1 while the speech announces") is the EXPECTED behavior of the shipped build, and the cause is (b) alone, independent of the session question. validate_apl.py cannot catch this class: the malformation is semantic (wrong property + unresolvable binding), not structural.

ITEM 2 DERISKED, CONSTRUCT (a) VERDICT: MECHANISM PROVEN, SESSION INTERPLAY SIM-UNVERIFIED (device round confirms). Positive control in the same runtime: handleTick + SetValue (the exact JF-624-proven shape) drove a utcTime counter on screen, advancing on schedule every 2s across the whole window, proving tick handlers FIRE on this document class in the official web runtime (reproduces the JF-624 claim for the slideshow document). The session-ending-Tell interaction is NOT exercisable in the sandbox (documented limitation: "The sandbox doesn't connect to a skill"), so that half stays a device probe. Spec chain making failure unlikely: tick events "occur as time passes" with no session coupling (apl-tick-event-handlers.html); the APL display subsystem is session-independent and dismissal is driven by the interaction timer / timeoutType, per the in-repo device-verified dossier claudedocs/research_apl_persistence_exhaustive_2026-05-25.md (whose PING pattern presupposes ticks outliving the session on display-only documents). Caveat for the device expectation: docs state running environments check tick handlers "at indeterminate intervals", so 8000ms is a floor, not an exact cadence.

RECOMMENDED FIX (PROVEN in the same Amazon runtime; minimal delta, ONE command object in SlideshowTemplate; NO production code changed by this probe, dispatch decision belongs to the orchestrator): handleTick stays, SetPage gains a sequencer (the documented fast-mode escape hatch) and becomes relative+1 with NO self-reference; navigation stays "wrap" so the wrap supplies the loop: {"type":"SetPage","sequencer":"slideshowAdvance","componentId":"slideshowPager","position":"relative","value":1}. Validation runs: (i) exact production document with ONLY this command changed advanced exactly one page per 8000ms tick (0s p0, 8s p1, 16s p2, 24s p3); (ii) at a 2000ms test cadence it cycled through all 5 pages and WRAPPED 4->0, then completed a second full cycle; (iii) control isolations: SetPage+sequencer+literal value 2 jumped 0->2 (sequencer promotion works), SetPage+sequencer+value=self-reference stayed frozen (the binding is the dead part), SetValue-from-tick advanced (ticks fire). User swipes still work alongside (worst case a swipe races the sequencer by one page).

FALLBACK CANDIDATES FROM THE PROBE LIST, TESTED: AutoPage fired from Pager onMount (normal mode) DOES advance (verified) but does NOT wrap: docs "Wrapping isn't supported" and "finishes after the last page"; empirically it stopped dead on the last image and stayed there. It yields a one-pass slideshow only, inferior to the SetPage fix unless non-looping is acceptable. AutoPage is ALSO "Ignored" in fast mode, so it cannot replace the tick command without the same sequencer treatment. Session-keep-alive shape: NOT needed for the advance mechanism (nothing couples ticks to the session; the fix works with the session-ending Tell in place, pending the device confirmation of (a)); only revisit if the device round refutes (a). Citations fetched live 2026-10-12: developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/ apl-set-page-command.html, apl-tick-event-handlers.html, apl-commands.html (fast-mode table + sequencer promotion rule), apl-auto-page-command.html, apl-pager.html (event.source.page), test-apl-examples-code-sandbox.html (sandbox limitations).

RE-DISPATCH VERIFICATION, NO-OP CONFIRMED (2026-10-17, implementation worker, branch jf329 rebased onto 9e5574b9): the dispatch text still described JF-329 as greenfield ("no Photo handling anywhere"), but the feature is fully shipped and merged on main: ShowPhotoAlbumIntent (merge f8802300), the sandbox-proven SetPage sequencer fix (66951471), and both filed follow-ups closed (JF-862 resolve-chain hoist in 117d8248, JF-863 doc-only boundary in d66a112d). No production change was needed or made. Full battery re-run at 9e5574b9 proves the merged state green: Release -warnaserror build 0 warnings 0 errors BOTH TFMs; suite 6041/6041 BOTH TFMs (0 failed, 0 skipped); validators models PASS at the unchanged 275-warning baseline (zero photo rows in it), locales PASS (no new gaps), versions PASS (0.12.1.0), APL PASS (the SetPage contract guard walks the shipped slideshow template), NLU dry-run 43 passed. DESIGN DIVERGENCE vs the dispatch text, documented not changed: the dispatch asked for a CrossMedia-style artist fallback (TryEntityFallbackAsync conventions) on photo not-found; the shipped handler answers the localized PhotoAlbumNotFound Tell with no music substitution (the code-review F4 choice: below-bar multi-match shows the TOP-RANKED photo album, announced by name). Rationale: the photo slot is not a greedy music-capturing slot (every slotted carrier names the photo media, the JF-403 optional-slot boundary), so the fallback's misroute precondition does not hold, and starting audio playback in answer to a photo query would be a wrong-surface substitution. DOCS CALL, now recorded (the dispatch asked for the mermaid decision to be documented): no existing graph owns photo browsing (verified: zero photo rows in docs/graphs.json and docs-site/data.json; the six documented flow families are playback-side), so the mermaid mirrors were correctly skipped at merge and only the voice-reference mirrors (VOICE_COMMANDS.md + docs/VOICE_COMMANDS_BY_LOCALE.md) carry the Photos group. REMAINING FOR CLOSURE: unchanged, the maintainer device round (probe items 1-3).
<!-- SECTION:NOTES:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

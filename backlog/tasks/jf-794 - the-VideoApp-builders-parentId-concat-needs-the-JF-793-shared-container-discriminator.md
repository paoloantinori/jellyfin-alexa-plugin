---
id: JF-794
title: >-
  JF-794 - the VideoApp builders' ParentId concat needs the JF-793 shared-container
  discriminator (the flag-on twin of the merge hazard)
status: Done
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-06'
labels:
  - bug
  - audiobooks
dependencies:
  - JF-793
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/PlaybackLaunchBuilder.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookItems.cs
priority: medium
---

## Description
<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-793 Finding 2 round (2026-10-06), same-turn per the
review-recommendation rule. The live minix census proved the shared-container shape
REAL (the "Audiobooks" library container at /data/media/audiobook/Audiobooks directly
holds 6 collapsed single-file books: The Honest Truth About Dishonesty, two HBR 10
Must Reads volumes, Managing Humans, Power Moves, Radical Candor), and JF-793 closed
the hazard on the AudioPlayer paged path by teaching
`AudiobookItems.TryResolveBookFolder` the direct-sit discriminator (a leaf whose file
does not sit DIRECTLY inside the resolved parent means the parent is a shared
container; the climb is rejected and the leaf plays as its own track).

The VideoApp builders do NOT go through that helper: `BuildVideoAppAudioResponse` and
`BuildAudiobookResumeResponse` concat the item's RAW ParentId into the
`/alexaskill/api/video-audio/audiobook/{parentId}/stream.m3u8` URL, so for one of the
6 collapsed books under the live container the concat endpoint enumerates the
CONTAINER recursively (the whole library) and serves every chapter of every book as
one "audiobook" timeline. This has ALWAYS been the flag-on behavior (JF-791 recorded
the default path becoming CONSISTENT with it rather than newly wrong), but with the
census evidence the shape is real, not hypothetical.

Fix shape to design in-task: route the builders' parentId through the same
discrimination (either call `AudiobookItems.TryResolveBookFolder` and fall back to
the item's own id on null, or extract the direct-sit predicate for the concat
decision), keeping the token-minting and chapter-scoped re-mint paths
(`StreamHlsVideoAudioCore`) coherent: the playlist URL's {parentId} drives BOTH the
enumeration and the tracker key. The JF-567/JF-694 tracker key shape
(`ResumeMath.GetAudiobookBookKey`: ParentId when present) must stay consistent with
whatever id the URL carries, or resume silently falls to 0. Red pin: the
shared-container fixture on the NativeControlsForBooks arm, expecting the leaf's own
id in the concat URL, not the container's.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors: Release -warnaserror build of the full solution, 0 warnings and 0 errors, both TFMs.
- [x] #2 dotnet test passes: full suite 5415/5415 on net10.0 and net9.0 (baseline 5403 + 12 new pins; 10 consecutive green full runs on net10.0 after the collection-serialization fix; two earlier single-failures whose names went uncaptured, with the plausible cause, the new test class racing the shared Plugin.Instance from outside the Plugin collection, structurally closed).
- [x] #3 No new compiler warnings introduced: the Release -warnaserror run is the proof (0 warnings).
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization: N/A, no session attribute shapes touched (the builders write no session state).
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress: N/A, no HttpClient changes.
- [x] #6 NLU test fixtures updated if interaction model changed: N/A, no interaction model, locale, or speech changes (the task's STOP condition for new strings never fired; the rejected-climb degrades reuse existing surfaces only).
- [x] #7 E2E test added for new intent or handler logic: N/A per the worker split (no deploy from this branch); the coverage is 12 unit pins across the builders, the chokepoint delegation, and the PlayBook handler, each with a red proof on the unmodified tree or an explicit hold-in-place rationale (the remap pin).
- [x] #8 Locale response strings added to all 17 locales: N/A, no new user-facing strings (see #6).
- [x] #9 /simplify passed: 4 parallel angles, 8 applied, 5 reasoned skips (the skip rationales, including the one the code-review round corrected of record, live in the simplify-tail commit message).
- [x] #10 /code-review high passed: 5 findings, all dispositioned. F1 the four missed book-capable threading sites APPLIED; F2 the stale container-timeline tracker mark APPLIED with a red proof; F3 the remapped-book asymmetry FILED as JF-799 with the music-safe-unification prerequisite and the device probe; F4 the hold-in-place remap pin APPLIED; F5 the fresh builder riding TryResolveBookFolder APPLIED.
<!-- DOD:END -->

## Final Summary

THE FIX: the two VideoApp builders (`BuildVideoAppAudioResponse`, `BuildAudiobookResumeResponse`) now resolve the concat's ParentId through the ONE JF-793 discriminator instead of climbing it raw. The shared seam is `AudiobookItems.TryResolveVerifiedParentFolder` (the type-agnostic climb: the ParentId resolves to a Folder AND the leaf file sits DIRECTLY inside it; a null library manager, a dangling ParentId, and the shared-container shape all fail CLOSED), with `TryResolveBookFolder` as the AudioBook-gated wrapper the search paths and the fresh builder use. When the climb ACCEPTS, the folder's Id IS the raw ParentId, so the accepted concat URL is byte-identical to the pre-fix mint; the discriminator only ever rejects. On rejection: the fresh launch plays the leaf as its own single-item `video-audio/{leafId}` stream, and the tracked resume degrades to the flat AudioPlayer chapter resume. The caller's `ILibraryManager` threads through the builder family, the audiobook launch composition, and the AudioPlayer chokepoint's native-controls delegation edge; every book-capable chokepoint call site whose handler holds a manager passes it (PlayBook, Yes, Resume, StartOver, LaunchRequest, ContinueWatching, GoToChapter, ProgressReporter, PlayFavorites, SkillConnection, SearchMedia, AplUserEventHandler, PlayRandom, Recommend, PlayLastAdded, FollowMe; Repeat excludes books by design; SkipForwardBack and JumpToPosition hold no manager and their unthreaded degrade is graceful, the hazard being closed everywhere by the fail-closed design itself).

RED PROOFS (on the behaviorally-unmodified tree: the plumbing commit added the inert parameter seam only; both TFMs): 6 pins failed. The builder fresh launch for the census collapsed book (it minted `audiobook/{containerId}`, the merge); the unresolvable-parent fresh launch (it minted the dead `audiobook/{dangling}`); the no-manager fail-closed fresh launch; the collapsed-book tracked resume (it minted the container playlist slice); the collapsed-book chokepoint delegation; and the PlayBook flag-on handler pin ("Not found: alexaskill/api/video-audio/{leafId}", meaning the container concat was minted). The 3 true negatives stayed green (verified-chapter fresh concat, tracked-resume slice, chokepoint delegation still concats). A seventh red proof landed in the code-review round: the stale beyond-runtime container-timeline mark saturated at the leaf runtime ("Expected: 0 / Actual: 1200000") before the JF-565 clamp + tracker-clear fix.

TRACKER-KEY COHERENCE (the JF-694 contract, held by construction): on the accepted path the URL segment IS `GetAudiobookBookKey`'s Guid (pinned); on the rejected path the record side (`RecordPositionProgress` keys the leaf under `GetAudiobookBookKey`) and every read still resolve through the one key resolver. The code-review F2 fix also heals the pre-fix poison: a merged-container timeline mark under the census book's key (which the monotonic tracker can never lower) now restarts the flat resume at 0 AND clears the key, instead of minting a dead end-of-stream offset.

THE MULTI-PART ADDENDUM DECISION: FILED as JF-798, same-turn, with the census evidence. The live census artifacts (/tmp/jf793_audiobook_leaves.json + /tmp/jf793_parents.json, re-run through /tmp/jf793_census.py during this task) show 15 distinct parents of AudioBook leaves: 14 book folders whose every child file sits DIRECTLY inside, and the one shared "Audiobooks" container with the 6 collapsed books. NO multi-part book (Part1/Part2) exists in the live library, and the outermost-ancestor walk's stop condition (book folder vs library container, both plain Folders in Jellyfin's type system) has no type evidence to read: not small, not provably behavior-preserving, and entirely untested by reality on this install.

THE CODE-REVIEW F3 FILING: JF-799 (the remapped-book fresh-vs-resume asymmetry). The fresh arm's isAudioBook gate serves Audio-typed remap chapters as single items while the resume builder's type-agnostic climb concats their folder; unifying either direction is unsafe without a music-safe discriminator (fresh) or accepts losing the JF-784 leg-3 seek bar (resume). Held in place by the new `PlayBook_TrackedResume_AudioTypedRemapChapter_ResumesViaFolderConcat` pin; the JF-567-class mis-slice hazard goes to the task with a device probe.

LEGACY PINS: 12 triaged. 11 fixture upgrades to the verified book-folder shape the fail-closed legitimately demands (refusal, token scope, slice, ledger, restart, session-held pairs); 1 honest supersede (the capability-gate bare-Audio resume pin that pinned the dead `audiobook/{ownId}` URL, replaced by the verified-fixture pin plus the unverified-degrades-flat sibling). Two more pins reshaped by the F2 clamp doctrine (the screenless beyond-runtime restart, and the degrade pins carrying runtimes). CLAUDE.md's audiobook section updated in the same change: the root-key sentence's rationale (the builder never mints the dead URL anymore) and the new builders-contract sentence.

SUITES: 5415/5415 both TFMs (baseline 5403 + 12 pins). Release -warnaserror 0/0. Gates: /simplify 4 angles (8 applied, 5 reasoned skips) + /code-review high (5 findings: 3 applied, 1 applied-as-pin, 1 filed as JF-799). Not deployed (worker branch only; the worker does not deploy).

GATE-MARKER ADDENDUM (2026-10-06, marker F3, same-turn): the ONE-LEVEL climb resolution means a multi-part book (chapters under Part1/, Part2/ subfolders) presents the PART folders as separate disambiguation entries, and head, confirm, and continuation all stop at the part boundary. The altitude fix (resolving to the outermost non-container BOOK ancestor instead of the one-level parent) belongs WITH this task's builders sync so both paths flip together; the JF-791 divergence note already flags subfolder under-resolution on the builders side.

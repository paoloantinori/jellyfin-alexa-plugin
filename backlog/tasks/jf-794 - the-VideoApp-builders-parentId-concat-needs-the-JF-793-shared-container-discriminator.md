---
id: JF-794
title: >-
  JF-794 - the VideoApp builders' ParentId concat needs the JF-793 shared-container
  discriminator (the flag-on twin of the merge hazard)
status: To Do
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

GATE-MARKER ADDENDUM (2026-10-06, marker F3, same-turn): the ONE-LEVEL climb resolution means a multi-part book (chapters under Part1/, Part2/ subfolders) presents the PART folders as separate disambiguation entries, and head, confirm, and continuation all stop at the part boundary. The altitude fix (resolving to the outermost non-container BOOK ancestor instead of the one-level parent) belongs WITH this task's builders sync so both paths flip together; the JF-791 divergence note already flags subfolder under-resolution on the builders side.

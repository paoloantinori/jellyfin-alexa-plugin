---
id: JF-797
title: >-
  JF-797 - the PlayBook normalization hardening residuals: the payload-kind gate, the
  lazy folder resolution, and the fresh-ask resume discriminator
status: To Do
assignee: []
created_date: '2026-10-06'
labels:
  - tech-debt
  - audiobooks
dependencies:
  - JF-793
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookItems.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-793 gate-marker (2026-10-06), findings 2, 5, and 6, same-turn per the
review-recommendation rule. Three hardening items on the normalization path JF-793 built,
none a live defect today:

1. THE PAYLOAD-KIND GATE (marker F2): AudiobookItems.IsBookDisambiguationPayload routes
   ANY non-MusicAlbum Folder carrying the MediaTypeAlbum disambiguation label to the
   PlayBook leg. The two live producers happen to emit AudioBook leaves and MusicAlbums,
   so the breadth is latent; but any current-or-future producer emitting a MusicArtist,
   MusicGenre, CollectionFolder, or playlist folder would reach the PlayBook leg, whose
   TryResolveBookFolder answers null and whose chapters query on the folder returns zero
   children, playing the folder through the single-file fallback: a broken launch where
   the pre-JF-793 album leg handled it. Fix shape: a kind deny-list (or a positive
   children-are-AudioBook probe) narrowing the payload gate to the two intended shapes,
   with a pin per denied kind.

2. THE LAZY FOLDER RESOLUTION (marker F5): NormalizeBookCandidates issues one
   GetItemById per search candidate on EVERY multi-candidate ask, before the fuzzy
   consumers narrow to a >=90 auto-play pick or a Take(3) prompt. A short title whose
   SearchTerm returns dozens of rows pays N folder fetches whose results mostly
   evaporate. Fix shape: dedup by ParentId first and resolve folders only for the
   surviving ids, or resolve lazily for the top-scored candidates.

3. THE FRESH-ASK RESUME DISCRIMINATOR (marker F6): the finding-4 deep-resume guard keys
   on the page-1 scan's (0,0) answer, which an unstarted multi-page book also produces,
   so every FIRST-EVER ask of a long book runs the unpaged recursive fetch and finds
   nothing (one bounded query on the hot fresh-play path inside the Alexa window; the
   trade is now stated in the block comment). Fix shape: a cheap any-Played/positioned
   flag check ahead of the fetch (or folding the discriminator into the head query).
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

JF-795 GATE-MARKER ADDENDA (2026-10-06, same-turn): (a) item 3's exposure DOUBLED - the deep-resume unpaged fetch on every first-ever multi-page ask now fires on BOTH the direct ask and the YesIntent confirm (the shared AudiobookPlayResolver.PlayBookAsync), making the cheap discriminator more valuable; (b) item 1's payload-kind breadth now has a wider observable effect - the JF-795 BooksEnabled confirm gate sits inside the same over-broad IsBookDisambiguationPayload, so with books disabled a MediaTypeAlbum-labeled confirm carrying an arbitrary non-album Folder answers the FeatureDisabled Tell instead of reaching the album leg (the gate is right for real books; the breadth is the root, unchanged).

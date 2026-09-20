---
id: JF-610
title: >-
  Carrier-strip and locale-prefix consolidation: 4th prefix copy (fallbacks
  diverge) + 4th strip mechanism (policies invisible at call sites) + 2 inline
  slot readers
status: To Do
assignee: []
created_date: '2026-09-20 19:20'
updated_date: '2026-09-20 19:21'
labels: []
dependencies:
  - JF-602
  - JF-607
references:
  - commit 560b484c
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaylistNameNormalizer.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/KeywordMatcher.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Locale/ResponseStrings.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of 560b484c (JF-600) consolidated the reuse/altitude findings. Counts verified in the review: (1) locale-prefix extraction is now the FOURTH private copy with divergent fallbacks: KeywordMatcher.cs:465 (Substring), PhoneticSynonymGenerator.cs:46 (in Alexa/Catalog/, range slice), ResponseStrings.cs:80 (returns string.Empty on a dashless locale, unlike the others which return the locale), PlaylistNameNormalizer.cs:77 (Split allocation) - the new class doc self-acknowledges the fourth copy and defers retirement to a fifth appearance with no owner. (2) This is the FOURTH standalone carrier-strip mechanism, with policies invisible at call sites: AlbumPlayService.cs:134 (JF-469, FALLBACK-ONLY), PlayVideoIntentHandler.cs:69 (JF-509, RAW-FIRST), PlaySongIntentHandler.cs:190 StripSongCarrierPhrase (unconditional/preemptive), and the new PlaylistNameNormalizer (preemptive, 10 locales). A future slot-carrier fix can copy the preemptive policy onto a title-bearing slot and break real titles like 'Called Out in the Dark'. (3) The strip loop shape itself (table + prefix StartsWith + trim) duplicates AlbumPlayService's TryStripLeadingAlbumCallingWord mechanics with different loop semantics. (4) PlayPlaylistIntentHandler.cs:80 and ShufflePlayIntentHandler.cs:70 hand-inline the TryGetValue-then-NormalizePlaylistName expression, bypassing both GetPlaylistSlotValue and BaseHandler.GetSlotValue (the JF-594 'one home for slot extraction'). (5) The normalizer's do/while loop with flag+break+recheck and the two-dictionary split (TrailingCarriers holds only ja+hi; hi appears in BOTH tables) is the densest control flow for what a flat StripOne-until-false loop does. SEQUENCE: land JF-602 (strip policy) and JF-607 (ja coverage) first; consolidating a policy that is about to change is wasted motion.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A shared locale-prefix helper exists and all four call sites (KeywordMatcher, PhoneticSynonymGenerator, ResponseStrings, PlaylistNameNormalizer) use it; the divergent dashless-locale fallbacks are reconciled deliberately (ResponseStrings returns Empty today, the others return the locale)
- [ ] #2 A shared carrier-strip core exists with per-feature word tables and an explicit policy parameter (fallback-only vs preemptive); AlbumPlayService, PlayVideoIntentHandler, PlaySongIntentHandler's StripSongCarrierPhrase and PlaylistNameNormalizer ride it
- [ ] #3 PlayPlaylistIntentHandler and ShufflePlayIntentHandler read the playlist slot through the shared reader (no inline TryGetValue-then-Normalize one-liners)
- [ ] #4 PlaylistNameNormalizer's strip loop is the simpler flat shape; hi's dual-table presence (नाम की/नाम का in both leading and trailing) is either unified or documented as deliberate
- [ ] #5 Full test suite green; each consolidated call site keeps its current behavior (pin with tests before moving)
<!-- AC:END -->

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

---
id: JF-323
title: >-
  Audit: Verify all SSML metadata insertion points use EscapeXml (reserved-char
  crash class)
status: Done
assignee: []
created_date: '2026-07-12 15:00'
updated_date: '2026-07-18 19:55'
labels:
  - reliability
  - ssml
  - audit
milestone: m-8
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs:1723'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Comparative finding from the 2026-07-12 competitor review: AskNavidrome shipped a fix adding SSML/XML reserved-character sanitization for track/artist/album names, a real bug class (classical music and non-English catalogs commonly contain `&`, `<`, `>`, quotes). This plugin ALREADY has `BaseHandler.EscapeXml` (`BaseHandler.cs:1723`) and uses it in ~8 places (ListPaginationHelper, YesIntentHandler, BrowseLibraryIntentHandler, SetReminderIntentHandler, QueryRecentlyAddedIntentHandler, FollowMeIntentHandler). So this is NOT a from-scratch gap — it's a coverage audit.

Task: audit every code path that inserts library-derived metadata (item names, artist, album, playlist names) into SSML/`<speak>` output and confirm EscapeXml (or equivalent) is applied. Any unescaped insertion of a name containing `&`/`<`/`>` produces invalid SSML and an Alexa `InvalidResponse` → user hears an error. Add a unit test with a metadata name containing reserved characters flowing through the main play/announce responses.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every SSML/<speak> output path that includes library-derived names is confirmed to escape reserved XML characters
- [x] #2 Any unescaped insertion found is fixed to use EscapeXml
- [x] #3 A unit test feeds a metadata name containing & < > \" through the primary play/announce responses and asserts valid, non-crashing SSML
- [x] #4 No double-escaping regressions on already-escaped paths
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-18 08:26
---
2026-07-18 (autonomous audit, not yet implemented): CONFIRMED the gap. GetSsml (BaseHandler.cs:854) does `string.Format(template, args)` raw — NO escaping (verified by reading the body). Callers must escape; ~4 do (LaunchRequestHandler:262/333, FollowMeIntentHandler:147, YesIntentHandler:203/222 — they wrap the name in EscapeXml), but ~10 do NOT — these pass RAW item/artist/album/match names into SSML, so a name with & < > yields invalid SSML -> InvalidResponse:
  - PlayRadioIntentHandler:104 (currentAudio.Name -> NowPlayingSsml)
  - RecommendIntentHandler:188 (item.Name -> RecommendPlayingSsml), :217 (item.Name -> NowPlayingSsml)
  - PlayRandomIntentHandler:157 (firstItem.Name -> NowPlayingSsml)
  - MediaInfoIntentHandler:541 (item.Name, artist, album -> TrackByArtistFromAlbumSsml), :548 (item.Name, artist -> TrackByArtistSsml). NOTE: MediaInfo:367/370 pass `descriptionSsml ?? description` (a pre-built SSML FRAGMENT) — do NOT blanket-escape that one.
  - DisambiguationHelper:58, :93, :144 (matchList[index].Name -> DisambiguatePromptSsml/NextSsml)
  - BaseHandler:1398 (selector(best), query -> FuzzyAutoPlayAnnouncementSsml), :1409 (query, selector(best) -> FuzzySuggestionPromptSsml)
Fix approach: per-site EscapeXml on the NAME/QUERY args (NOT root-cause in GetSsml — some args are pre-built SSML fragments like MediaInfo descriptionSsml, which blanket-escaping would corrupt). Remove nothing from the already-escaping callers. Test: feed a name like 'Rock & Roll <Live>' through a representative announce path and assert the Ssml contains '&amp;'/'&lt;' (escaped), not raw '&'/'<'. Scope ~10 sites across 6 files + the SSML-fragment care -> substantial; left for a focused implementation session.
---

created: 2026-07-18 10:15
---
2026-07-18: IMPLEMENTED on branch jf-323-ssml-escape — per-site EscapeXml on all ~12 unescaped GetSsml name-args (PlayRadio, Recommend×2, PlayRandom, DisambiguationHelper×3, MediaInfo TrackByArtist×2 + the plain description fallback at 367/370, BaseHandler fuzzy×2). NOT root-caused in GetSsml (some args are pre-built SSML fragments like MediaInfo descriptionSsml — blanket-escape would corrupt). SSML fragments left untouched; only name/plain args escaped. 2 tests added (EscapeXml unit + GetSsml-with-escaped-name -> valid SSML). Verified: Release build 0-warning, full suite 2524 pass, completeness grep (every GetSsml name-arg now escaped, no double-escape). PR #17 opened — CI gating; merge pending. AC#1-4 met locally.
---

created: 2026-07-18 19:55
---
STATUS CLOSE (2026-07-18): PR #17 merged (f23738f) + follow-ups 1318c2c (E2E SSML-well-formedness test) and 08945d4 (/code-review high gaps: MediaInfo artistInfo concat, EscapeXml null-guard, PlayRadio radioMsg). The 'merge pending' note in comment #2 is resolved — all 4 ACs met, full suite green, /code-review high passed. Marking Done.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
SSML reserved-char escaping audit complete and merged. All ~12 previously-unescaped GetSsml name-args across 6 files (PlayRadio, Recommend x2, PlayRandom, DisambiguationHelper x3, MediaInfo TrackByArtist x2 + plain description fallback, BaseHandler fuzzy x2) now wrap library-derived names in EscapeXml; pre-built SSML-fragment args (MediaInfo descriptionSsml) were correctly left untouched. Delivered via PR #17 (f23738f) with follow-up commits 1318c2c (E2E SSML-validity coverage) and 08945d4 (code-review-high gaps closed). All 4 ACs met; full suite green.
<!-- SECTION:FINAL_SUMMARY:END -->

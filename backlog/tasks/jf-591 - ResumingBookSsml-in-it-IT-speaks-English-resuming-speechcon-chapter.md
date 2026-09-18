---
id: JF-591
title: ResumingBookSsml in it-IT speaks English ("resuming" speechcon + "chapter")
status: Done
assignee: []
created_date: '2026-09-18 20:05'
updated_date: '2026-09-18 21:37'
labels:
  - bug
  - i18n
  - ux
milestone: Polish
dependencies: []
references:
  - claudedocs/research_alexassml-voice-tags_2026-09-18.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Inventory during the SSML research (claudedocs/research_alexassml-voice-tags_2026-09-18.md) found that ResumingBookSsml in the it-IT locale file reads <say-as interpret-as="interjection">resuming</say-as><break time="200ms"/><emphasis level="moderate">{0}</emphasis>, chapter {1}. That is an English speechcon (resuming is not in the it-IT speechcon list, so Alexa likely reads it literally) and the English word "chapter" spoken to Italian users. Fix the it-IT wording (and audit the other it-IT keys for similar English residue while there). NOTE: JF-590 removes the emphasis wrapper from this same string in all 17 locales; whichever task lands second must be applied on top of the other, no merge of intent, both end states keep only one wrapper-free shape.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 ResumingBookSsml in it-IT.json contains no English speechcon and no English prose: the interjection and the chapter wording are Italian (e.g. an it-IT-valid speechcon or plain text, and something like capitolo {1})
- [x] #2 Any other key in it-IT.json containing English residue found by the same audit is fixed in the same change or explicitly noted as deliberate (quote/track title placeholders excepted)
- [x] #3 The chosen Italian wording exists in it-IT speechcon reference if a speechcon is used; plain text is the fallback if none fits
- [x] #4 Tests pass on both TFMs without --no-build; no assertions depend on the English wording
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped in e5e3715a: the it-IT resume/restart announce family now speaks Italian. ResumingBook/ResumingBookSsml use the established it-IT speechcon 'eccoci' + 'capitolo {1}' (matching ResumingSsml/FollowMeSuccessSsml); ResumingVideo reads 'dalla posizione {1}' (arg is ResumeMath localized words); RestartingContent, FolderNoPlayableContent and NoMediaToRestart italianized after the extended audit found them (same class, fixed in-change per AC#2; the word-list scan false-positives on <say-as> tags and all'apostrophe forms were dismissed). One test updated: SpeechBuilderEpisodeAnnounceTests resume-arm pinned the old English it-IT value (red-first: failed 1/4113 both TFMs before the fix). Deliberate keep: English loanwords in Italian (album, ok). Gates: code-review high CLEAN (one out-of-scope finding became the NoMediaToRestart fix); /simplify exempt (data-only strings); suite 4113/4113 both TFMs; Release 0 warnings; validate_locales PASS.
<!-- SECTION:FINAL_SUMMARY:END -->

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

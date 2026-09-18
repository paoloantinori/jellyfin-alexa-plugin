---
id: JF-590
title: >-
  Remove the emphasis SSML wrappers from title announcements (plain title +
  existing breaks, all 17 locales)
status: Done
assignee: []
created_date: '2026-09-18 20:04'
updated_date: '2026-09-18 21:03'
labels:
  - enhancement
  - ssml
  - ux
milestone: Polish
dependencies: []
references:
  - claudedocs/research_alexassml-voice-tags_2026-09-18.md
  - >-
    backlog/tasks/jf-559 -
    Extend-amazon-musicArtist-SSML-role-to-remaining-artist-announcements-after-live-validation-of-the-FoundArtistInstead-pilot.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Every title announcement currently wraps the title placeholder in <emphasis level="moderate"> (12 sites per locale, identical shape in all 17 locale JSONs under Jellyfin.Plugin.AlexaSkill/Alexa/Locale/). Amazon's SSML Reference (Aug 28, 2025) defines emphasis as louder AND SLOWER, and warns that any emphasis-wrapped span is rendered by a legacy TTS system that "might change the speech sound quality". The user dislikes the resulting slowdown. Research (claudedocs/research_alexassml-voice-tags_2026-09-18.md) concludes the Amazon-endorsed pattern is plain title delimited by pause; every affected string ALREADY carries a <break> adjacent to the title, so the change is to delete the emphasis wrappers and keep the breaks and prose untouched. Affected keys (verify with grep before editing): NowPlayingSsml, NowPlayingWithPositionSsml, DisambiguatePromptSsml, DisambiguateNextSsml, RecommendPlayingSsml, FuzzySuggestionPromptSsml, FuzzyAutoPlayAnnouncementSsml, CrossMediaArtistOfferSsml, ResumePromptSsml, ResumingSsml, FollowMeSuccessSsml, ResumingBookSsml (plus any other emphasis hits the grep finds). Apply the same deletion to every locale file, not only it-IT/en-US. The mild prosody-rate fallback (rate 90-95% on the title only) is deliberately OUT of scope: it is only worth trying if the plain shape feels too flat on a real device, so it must not ship in this change.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 No locale JSON in Alexa/Locale/ contains <emphasis> (grep across all 17 locales returns 0 matches)
- [x] #2 Every title placeholder ({0}/{1} title args) that previously sat inside an emphasis wrapper is still preceded by its existing <break> tag; no break tags were removed or retimed as part of this change
- [x] #3 Only the emphasis wrappers changed per string; surrounding prose, placeholders and format-arg order are byte-identical otherwise
- [x] #4 All tests pass on both net9.0 and net10.0 without --no-build; any test asserting on emphasis markup is updated to assert the new plain shape
- [x] #5 dotnet build Release shows 0 warnings
- [x] #6 prosody rate fallback is NOT introduced; the option is recorded in the task notes with its device-verification precondition
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-18 21:05 implementation record. Change: 204 emphasis wrappers removed (12 title keys x 17 locales, single sed-class replace, zero variants found beyond level="moderate"); hi-IN ResumingSsml additionally gained <break time="200ms"/> after the title because it was the ONLY locale where the wrapper was the sole separator (title-first Devanagari sentence; removal would have glued title to prose). AC#2 precision note from code review: ar-SA and ja-JP ResumingSsml carry no break at all (they never had one and their templates did not fuse on removal); all other locales kept their pre-existing breaks untouched. Guard: LocaleStringsTests.ResponseStrings_ContainNoEmphasisTag sweeps the raw embedded locale resource of every locale (auto-covers locale 18 and any future key), case-insensitive; proven red/green by injecting a wrapper into fr-FR (1 failed exactly fr-FR, restored). TestHelpers.GetSpeechText dropped the two dead emphasis-stripping lines (no producer of the tag remains repo-wide). AC#6: the prosody-rate fallback (rate 90-95% on the title span only) is deliberately NOT shipped; it is the documented upgrade path ONLY if the plain shape feels flat on a real device (perceptual evidence is thin: no study measures rate changes on a title span, 5% is below documented thresholds). Revisit decision belongs to an on-device listen, not a code change. Gates: /simplify 4-angle pass (4 findings applied: centralized guard, dead stripper lines, per-test assertion dedup, hi-IN delimiter; skip: jf-55 historical record left immutable), code-review high CLEAN (no finding >= 80; two sub-threshold notes actioned: guard made case-insensitive, ar-SA typo filed as JF-592), suite 4113/4113 both TFMs, Release 0 warnings, validate_locales PASS.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped in 185c2ff3 (deployed same evening): all 204 <emphasis level="moderate"> wrappers removed from the 12 title announcement keys across all 17 locales; titles are now plain text delimited by their existing break tags per the Amazon-endorsed pattern documented in claudedocs/research_alexassml-voice-tags_2026-09-18.md. hi-IN ResumingSsml gained <break time="200ms"/> after the title (the only locale where the wrapper was the sole separator). Regression guard: LocaleStringsTests.ResponseStrings_ContainNoEmphasisTag, a case-insensitive raw-resource sweep of every locale (auto-covers a future locale 18 and any future key), proven red/green by wrapper injection into fr-FR. TestHelpers.GetSpeechText dropped the dead emphasis-stripping lines. The prosody-rate fallback is recorded in the notes as the on-device-listen upgrade path and deliberately not shipped. Follow-ups filed from the review: JF-591 (ResumingBookSsml English residue, pre-existing), JF-592 (ar-SA album typo, pre-existing).
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

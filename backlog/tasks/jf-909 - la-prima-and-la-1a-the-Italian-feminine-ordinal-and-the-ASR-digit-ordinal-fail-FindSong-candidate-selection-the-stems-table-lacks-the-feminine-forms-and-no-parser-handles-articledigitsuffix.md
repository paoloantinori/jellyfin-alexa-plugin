---
id: JF-909
title: >-
  'la prima' and 'la 1a' (the Italian feminine ordinal and the ASR
  digit-ordinal) fail FindSong candidate selection: the stems table lacks the
  feminine forms and no parser handles article+digit+suffix
status: Done
assignee: []
created_date: '2026-10-19 12:45'
updated_date: '2026-10-19 13:00'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
DEVICE EVIDENCE 2026-10-19 14:37 (Paolo's loop-test round, minix logs): during FindSong candidate selection ('Ho trovato 2 canzoni. 1. The Idiot Kings, 2. I'm Livin' on Baby Food. Quale?'), Paolo said 'la prima' and the ASR delivered titleKeywords='la 1a' (corr 9941f5d3): the picker answered 'Non ho capito. Di' il numero o il titolo della canzone.' A bare '1' worked on the retry (corr 1cbc90d5). ROOT CAUSE (DisambiguationHelper.OrdinalStemsByRank): rank-0 stems are {first, primo, erste, premier, primera, primeira} - the Italian FEMININE 'prima' is MISSING (the user refers to 'la canzone', feminine; 'primo' is masculine). And the ASR's digit-ordinal rendering 'la 1a' (article + digit + ordinal suffix) has NO parser at all: int.TryParse fails, the single-token cardinal/ordinal check fails (multi-token), the title match fails, and no stem matches the digit form. FIX: (1) add the Italian feminine ordinals to the stems: prima/seconda/terza/quarta (ranks 0-3); (2) add a digit-ordinal parser for the ASR shape 'la 1a'/'il 1o'/'la 2a' etc.: strip leading articles and trailing ordinal suffixes (a/o/°/ª), then int.TryParse the remaining digit (rank 1-4 only, matching the direct-digit cap). BOTH fixes in DisambiguationHelper, shared by every picker that uses ResolvePick (FindSong, album disambig, etc.). AUTOMATIC TEST (Paolo asked): a unit battery on ResolvePick covering 'la prima', 'il primo', 'la 1a', 'il 1o', 'la 2a', 'la seconda', the existing bare digits, and the no-regression side (a title containing 'prima' still matches as title first - the title-before-ordinal order must hold).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
MAINTAINER CORRECTION 2026-10-19 ~14:4x: Paolo actually answered 'il primo' (masculine, which IS in the stems table) - the ASR produced 'la 1a' anyway (a GENDER-SWAPPED digit-ordinal rendering). This makes the digit-ordinal parser the PRIMARY fix (the ASR's digit rendering cannot be predicted from what the user said); the feminine word forms remain worth adding for the word-form transcription path. The fix is the same: DisambiguationHelper needs both the word stems AND the digit-ordinal shape.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED inline (trivial class: 4 stems-table strings + 1 bounded parser + 15-test battery), committed af229cd3, pushed, DEPLOYED to minix AlexaSkill_1.0.0.0 (md5 3720b860 active, TryParseDigitOrdinal verified in the running DLL via strings, config survived). THE FIX: (1) the Italian feminine ordinals (prima/seconda/terza/quarta) join OrdinalStemsByRank so the WORD form 'la prima' resolves to rank 0; (2) the new TryParseDigitOrdinal handles the ASR's DIGIT-ordinal rendering (the device evidence: Paolo said 'il primo', the ASR produced 'la 1a' - a gender-swapped article+digit+suffix shape no word stem can match): strips the leading article (la/il/lo/le/l'/the/el/der/die/das), strips the trailing ordinal suffix (a/o/ª/º/°), parses the surviving digit under the same 1-4 cap. Placement: between the direct-digit check and the single-token check (a digit-ordinal is unambiguously a rank, never a title, so it runs BEFORE title matching - unlike word ordinals which run AFTER). No-regression: the title-before-ordinal order for WORD forms holds (pinned: 'prima donna' still matches the candidate title). Suite: 6065/6065 BOTH TFMs (6050 + 15 new). gate-exempt: trivial class, justified in the commit.
<!-- SECTION:FINAL_SUMMARY:END -->

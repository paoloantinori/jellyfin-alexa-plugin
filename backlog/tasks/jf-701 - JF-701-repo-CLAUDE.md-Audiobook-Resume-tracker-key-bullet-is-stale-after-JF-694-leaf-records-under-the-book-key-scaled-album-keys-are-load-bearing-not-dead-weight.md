---
id: JF-701
title: >-
  JF-701 - repo CLAUDE.md Audiobook-Resume tracker-key bullet is stale after
  JF-694 (leaf records under the book key, scaled; album keys are load-bearing,
  not dead weight)
status: Done
assignee: []
created_date: '2026-10-02 03:57'
updated_date: '2026-10-02 06:01'
labels:
  - documentation
  - audiobooks
  - tracking
dependencies: []
references:
  - >-
    backlog/tasks/jf-694 -
    One-chapter-book-resume-never-gets-a-position-to-serve-tracker-write-gate-is-Folder-only-while-the-single-chapter-redirect-keys-segments-by-the-chapter-leaf.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
<!-- SECTION:DESCRIPTION:BEGIN -->
Filed same-turn 2026-10-02 from the JF-694 code-review round (high effort, finding 2 of 6; not applied in JF-694 because the repo CLAUDE.md is outside that worker's declared file scope). The Audiobook Resume reference section in the repo CLAUDE.md still states the pre-JF-694 record contract: "**Tracker key is the book parent-folder GUID**: `GetSegment` records segments keyed by the URL `itemId` (= parentId). Resume lookups use `item.ParentId`. `AudiobookPositionTracker.NormalizeKey` canonicalizes both to GUID "N" format - do not bypass it...". JF-694 widened the record gate: `VideoAudioController.RecordPositionProgress` now also records an AudioBook LEAF with a non-empty ParentId (the one-chapter redirect's shape) under `ResumeMath.GetAudiobookBookKey(item)` (the ParentId) with the song core's 4s segment index translated onto the tracker's 10s concat timeline via `AudiobookPositionTracker.RecordScaledSegment` (a raw 4s index read as 10s would resume 2.5x past the listening point). The root-level single-file AudioBook (empty ParentId) is deliberately still unrecorded (its mint would build audiobook/{ownId}, which the concat endpoint 404s on). ALSO fold in the adjacent fact correction discovered in the same round: the JF-694 task description's claim that "album resume sums track offsets via AlbumPlayService rather than reading the tracker" is FALSE; AlbumPlayService.cs ~630 reads the tracker (JF-625 criterion 3, seek-mode resume truth overrides UserData), so the CLAUDE.md audiobook-resume section should state album keys are load-bearing. The one-definition docs live on RecordPositionProgress (VideoAudioController.cs) and RecordScaledSegment (AudiobookPositionTracker.cs); the CLAUDE.md bullet should be updated to match, keeping it the verified reference it is maintained to be.
<!-- SECTION:DESCRIPTION:END -->

<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (N/A: doc-only diff, CLAUDE.md is not a build input)
- [x] #2 dotnet test passes (N/A: no code changed)
- [x] #3 No new compiler warnings introduced (N/A: no code changed)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session code touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: no handler logic changed)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing strings)
- [x] #9 /simplify passed (no blocking cleanups remaining) (ran: 4 angles; applied the bullet split, the read-side correction, the vocabulary fix; pointer-only trim and mechanics shave skipped with evidence-based reasons)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked) (exempt: documentation-only diff per the completion-gates exemption; every fact in the bullet was adversarially verified in JF-694's own code-review round, where this finding was filed; the /simplify round additionally caught and fixed one stale claim)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Doc-only closure, done inline by the orchestrator. The repo CLAUDE.md Audiobook-Resume section now states the post-JF-694 record contract as three one-rule bullets (house /simplify round applied: the packed single bullet was split to match the section's single-claim-title convention, the LOAD-BEARING album-keys warning lifted out of nested parentheses, and the vocabulary aligned to the file's "single-chapter" wording). Content: the one write gate (VideoAudioController.RecordPositionProgress) with the Folder arm and album keys named LOAD-BEARING (AlbumPlayService reads the tracker in seek mode, the JF-694 fact correction); the single-chapter AudioBook-leaf arm recording under ResumeMath.GetAudiobookBookKey with the 4s-to-10s RecordScaledSegment translation and the deliberately-cold root-level leaf (a warm root key arms the dead audiobook/{ownId} URL); and the key-agreement rule CORRECTED during the gate round (the altitude finding, verified against ResumeMath.cs:415 and all five handler read sites): every resume read resolves the key through GetAudiobookBookKey, not raw item.ParentId as the pre-JF-701 text claimed. Skipped with reasons: the pointer-only trim and the numeric-mechanics shave (both refuted by the reuse and efficiency angles on neighbor-bullet evidence; the depth was the JF-694 gate-marker's explicit disposition). No code changed; build/test/deploy not applicable; no deploy made.
<!-- SECTION:FINAL_SUMMARY:END -->

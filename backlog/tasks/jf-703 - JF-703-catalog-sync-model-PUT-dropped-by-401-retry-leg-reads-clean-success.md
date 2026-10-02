---
id: JF-703
title: >-
  JF-703 - catalog sync: a model PUT dropped by the 401-retry reads as a clean
  locale completion (pre-existing injection-gate shape, re-filed by the JF-695
  review)
status: To Do
assignee: []
created_date: '2026-10-02 12:00'
labels:
  - catalog
  - resilience
dependencies:
  - JF-695
references: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn by the JF-695 worker (number reserve; max existing was
JF-701). NOT applied inside JF-695: the shape is PRE-EXISTING (the injection gate
plus the JF-513.3 per-run payload-hash skip predate it); JF-695's diff only touched
the hunk and adds a frozen-type variant of the same trigger.

The sequence (code-review high finding, verified against
LibrarySyncService.RunLegAsync + SyncCatalogForLocaleAsync):

1. Attempt 1 of a locale leg uploads artist+album+series (each payload hash is
   recorded in uploadedPayloadHashes BEFORE the upload), then the model PUT gets a
   401 -> refresh + one full-leg retry.
2. Attempt 2: all three payloads hash-match the attempt-1 entries, so
   SyncCatalogForLocaleAsync skips all three uploads and returns Version null for
   each (the JF-513.3 no-op contract).
3. The injection gate (`artistVersion != null || albumVersion != null ||
   seriesVersion != null`) sees all-null and SKIPS the PUT entirely.
4. The leg returns succeeded. This locale's live model was never updated with the
   attempt-1 versions (those ARE minted on SMAPI, but the model still references
   the old versions), and no log line marks anything amiss.

JF-695's contribution is only reachability shape: a frozen type also contributes a
null version in that window, and pre-JF-695 the frozen artist aborted attempt 1
wholesale so attempt 2 genuinely re-uploaded everything. Without a frozen type the
same drop is reachable today with zero JF-695 involvement.

Fix sketch (when picked up): track "a model PUT was attempted for this locale but
not completed" across the attempt loop; when attempt 2 returns all-null versions
after such an attempt, treat the leg as failed (or re-mint instead of hash-skipping)
so the leg cannot read as a clean completion. Trigger window is narrow (the 401
must land exactly on the PUT), hence low priority.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] The locale-leg attempt loop distinguishes "PUT completed" from "PUT skipped after a prior PUT attempt", and the latter fails the leg (or re-mints the skipped uploads) deterministically
- [ ] A pin exists for the sequence: attempt-1 PUT 401 -> attempt-2 all-null versions -> leg reports failure
- [ ] dotnet build 0 errors, dotnet test green both TFMs, no new warnings
<!-- DOD:END -->

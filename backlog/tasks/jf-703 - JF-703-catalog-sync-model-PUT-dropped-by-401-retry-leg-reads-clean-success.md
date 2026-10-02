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

AUDIT ADDENDUM (2026-10-02, JF-706 gate-marker round, pre-existing but this family):
the hash-skip records the payload hash BEFORE the upload (LibrarySyncService.cs ~596,
upload at ~608), so "already uploaded this run" is really "already attempted this run":
a failed upload followed by the leg-level 401 retry hash-skips a version that was never
minted, dropping a needed upload for the rest of the run (attempt 2 sees the hash at
~586 and returns count/null; other types mint, the PUT omits this id, the locale logs
completed). Self-heals only on the next full sync (fresh dictionary). The in-code
comment at ~578-582 ("the last minted version ... is the one the live model already
references") is FALSE in this shape. Same commit's JF-709 audit extends the starvation
set: the equivalence-class FIRST member can itself be hash-skipped after a
failed-upload retry. Fold this ordering fix into this task's fix shape.
FOLDED INTO JF-709 (2026-10-02, picked up together): the addendum's ordering hazard
(hash-record-before-upload) is fixed by the JF-709 change - the hash is recorded only
after UploadCatalogValuesAsync returns successfully, and the false ~578 comment is
corrected in the same touch. THIS task's remaining scope is the CORE sequence only:
the PUT-completed-vs-skipped distinction across the locale attempt loop when the 401
lands on the model PUT itself (attempt-1 uploads all succeed, attempt-2 hash-skips
them all, the injection gate skips the PUT).
AUDIT ADDENDUM 2 (2026-10-02, JF-709 code-review high round; both pre-existing
retry-shape residuals, same root as the CORE, so they land here rather than as new
tasks): (a) the PUT-stage-401 sequence above also produces NO ledger write and a
green leg - the stale-green-row hole JF-709 closed for the freeze shape is equally
reachable via this retry path; fix it with the CORE (the attempted-PUT tracking
makes the leg fail) or extend JF-709's no-PUT writer to it. (b) A 401 on ONE type's
upload mid-leg leaves the earlier types hash-recorded, so the retry's PUT wires ONLY
the retried type's catalog - on a fresh install the model gets no valueSupplier for
the silently skipped types while the run reports Success; the attempted-upload
tracking must cover the type legs, not only the PUT.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] The locale-leg attempt loop distinguishes "PUT completed" from "PUT skipped after a prior PUT attempt", and the latter fails the leg (or re-mints the skipped uploads) deterministically
- [ ] A pin exists for the sequence: attempt-1 PUT 401 -> attempt-2 all-null versions -> leg reports failure
- [ ] dotnet build 0 errors, dotnet test green both TFMs, no new warnings
<!-- DOD:END -->

---
id: JF-787
title: >-
  JF-787 - the during-encode foreign-timeline serves' position-tracker
  poisoning: the residual dimension that outlives the window
status: Done
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-10 17:18'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-784 -
    the-concat-caches-scope-blindness-and-the-unbounded-token-scope-rendering-the-two-JF-767-code-review-residuals.md
  - >-
    backlog/tasks/jf-767 -
    JF-763-review-round-out-of-scope-findings-the-YesIntent-confirmation-path-siblings-and-the-userless-concat-endpoint-vs-the-scoped-paged-path.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-784 /code-review round (2026-10-06, effort high; finding F4, verified in
source before filing).

JF-784 leg 1 closed the COMPLETED-cache cross-scope serve: the concat cache-hit verdict now
compares the encode-metadata sidecar against the requesting scope's live enumeration and
re-encodes on a mismatch. The DURING-ENCODE windows stayed open as a documented, accepted
residual (the own-live unread row in `ValidateHlsCacheAsync` and the concurrent-encode
prewrite/live guard both serve whatever encode is running, bounded by the encode duration).
The review round found one dimension of that residual that does NOT stay inside the bounded
window: the POSITION TRACKER WRITE.

The shape: scope A's encode is live for a shared album/book; a scope-B request arrives,
is served the running encode's foreign listing (the accepted residual), and B's device
fetches segments. `GetSegment` → `RecordPositionProgress` sees a Folder parent and
`AudiobookPositionTracker.RecordSegment` writes the high-water mark (index x 10s) under the
SHARED book/album key, but against A's timeline. After the encode exits, B's next request's
timeline verdict invalidates the foreign entry and re-encodes under B's membership; the
stored A-timeline offset then maps onto B's timeline, so B's resume slice lands at unrelated
content (or past the end) until a later position overwrite replaces it.

Root fix shape (needs its own design pass): give the tracker a timeline-identity axis (e.g.
key positions by the same (chapterCount, durationTicks) identity the JF-784 verdict uses, so
a mismatched entry reads as absent instead of poisoning), or gate the during-encode serve
rows themselves on the timeline identity (the heavier JF-784-rejected shape: a gate through
the JF-782-hardened unread row and the prewrite guard). The first shape is the smaller
change: the tracker is plugin-owned state and the identity pair is already written into the
encode-metadata sidecar at encode start.

Verification bar: a red proof seeding a tracker position under one timeline identity and
reading it under another (today it serves the stale offset; post-fix it reads as absent and
resume falls back to 0), plus the existing tracker pins green.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 The tracker's read/write keyed (or gated) by timeline identity so a
      foreign-timeline position cannot poison a re-encoded timeline's resume, with the red
      proof above, or the residual re-adjudicated with the reasoning recorded here
<!-- DOD:END -->

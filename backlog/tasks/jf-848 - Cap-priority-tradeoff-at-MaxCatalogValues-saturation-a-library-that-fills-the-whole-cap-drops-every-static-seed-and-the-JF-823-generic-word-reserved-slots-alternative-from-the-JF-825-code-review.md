---
id: JF-848
title: >-
  Cap-priority tradeoff at MaxCatalogValues saturation: a library that fills the
  whole cap drops every static seed and the JF-823 generic word (reserved-slots
  alternative from the JF-825 code review)
status: To Do
assignee: []
created_date: '2026-10-09 16:59'
updated_date: '2026-10-09 19:55'
labels:
  - catalog-sync
  - review-followup
dependencies: []
references:
  - >-
    backlog/tasks/jf-825 -
    CatalogPayload.FromItems-uploads-duplicate-same-titled-items-as-distinct-values-and-the-MaxCatalogValues-warning-claims-a-truncation-that-never-happens.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed by the JF-825 worker (2026-10-09) from the /code-review high gate, finding F1 — NOT applied, deliberately.

JF-825 implemented the specified cap policy: the truncation drops the LOWEST-priority tail, seeds and generic words first, then the library tail (payload order IS priority order; CatalogPayload.TruncateTo tail cut). Consequence at the extreme: when a type's deduped library values reach MaxCatalogValues (50000), the tail cut drops ALL appended static entries — the JF-823 generic word ('Hörbuch', 'audiobook', ...) and every seed title — so for a library of 50000+ distinct audiobooks (or artists/albums) the bare generic-word requests the JF-823 live A/B verdict fixed ('lies ein hörbuch') would degrade to NO_SELECTION again.

The code-review's alternative: RESERVE the static slots — cut the library tail to (cap − staticAppendedCount) so the seeds and generic word always survive, at the cost of up to ~23 real library items for the largest libraries.

Why not applied in JF-825: (1) the drop order was explicitly specified by the dispatch (seeds/generic words first); (2) the degradation window needs >50000 DISTINCT values of one type — vanishingly rare (the JF-823 degradation lived in the opposite regime, near-empty audiobook vocabularies); (3) the truncation warning names exactly what dropped, so the state is diagnosable; (4) which side deserves the last 23 slots at a 50000-value cap is a product call (real user content vs generic fallback vocabulary), not a bug fix.

Trigger to revisit: any evidence of a real library near the 50000-value cap on any synced type, or a maintainer preference for generic-word availability over tail library items. The one-site change lives in LibrarySyncService.SyncCatalogForLocaleAsync (the TruncateTo call); the cap tests in Jellyfin.Plugin.AlexaSkill.Tests/Catalog/LibrarySyncServiceCatalogCapTests.cs pin the current order and must flip with it.
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
Trigger evidence check (orchestrator, 2026-10-09 20:05, live read-only): /Items/Counts on the minix household library returns ArtistCount 1135, AlbumCount 886, SeriesCount 140, BookCount 6 (SongCount 12766, but songs are not a synced catalog type). The largest synced type sits at ~2.3% of the 50000-value cap, so the saturation regime this task trades against is not merely rare here, it is two orders of magnitude away. Task stays parked per its own revisit trigger (evidence of a real library near the cap, or maintainer preference for generic-word availability); this data point is the counter-evidence side of that ledger.
<!-- SECTION:NOTES:END -->

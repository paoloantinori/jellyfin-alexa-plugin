---
id: JF-101
title: Investigate EF Core query warnings in Jellyfin logs
status: In Progress
assignee: []
created_date: '2026-05-08 20:50'
updated_date: '2026-05-08 21:02'
labels:
  - investigation
  - performance
  - bug
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two EF Core warnings are appearing repeatedly in Jellyfin server logs from the AlexaSkill plugin:

**Warning 1 — Missing OrderBy with Skip/Take**
```
[WRN] The query uses a row limiting operator ('Skip'/'Take') without an 'OrderBy' operator. This may lead to unpredictable results. If the 'Distinct' operator is used after 'OrderBy', then make sure to use the 'OrderBy' operator after 'Distinct' as the ordering would otherwise get erased.
```

**Warning 2 — Multiple collection includes without QuerySplittingBehavior**
```
[WRN] Compiling a query which loads related collections for more than one collection navigation, either via 'Include' or through projection, but no 'QuerySplittingBehavior' has been configured. By default, Entity Framework will use 'QuerySplittingBehavior.SingleQuery', which can potentially result in slow query performance.
```

**Investigation scope:**
- Identify which plugin queries trigger these warnings (likely in CatalogManager, LibrarySyncService, or handler code that queries Jellyfin's DB via EF Core)
- Determine if the Skip/Take without OrderBy could produce non-deterministic results (e.g., library sync returning different items on each run)
- Assess whether multiple Includes need `.AsSplitQuery()` for acceptable performance on large libraries
- Propose fixes: add explicit `.OrderBy()` before `.Skip()/.Take()`, add `.AsSplitQuery()` where multiple Includes are used
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Root cause queries identified (file + line number for each warning)
- [ ] #2 Fix proposed for missing OrderBy with reproducible ordering
- [ ] #3 Fix proposed for multiple Includes (either AsSplitQuery or refactored includes)
- [ ] #4 Changes do not break existing unit or integration tests
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

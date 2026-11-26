---
id: JF-967
title: >-
  One-click catalog recovery (admin action + incident report/runbook): the
  manual XML-surgery path from the deletion incident must not be the recovery of
  record
status: In Progress
assignee:
  - orchestrator
created_date: '2026-11-25 15:13'
updated_date: '2026-11-26 17:44'
labels:
  - catalog
  - reliability
  - incident-followup
  - ux
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-11-25 catalog-deletion incident: the recovery required manual XML surgery inside the podman container (the container has no python3; sed alternation broke on pipe chars; the podman cp dance with chown abc). That path is fragile and undocumented. (1) ADMIN ACTION: a one-click recovery surface — either a config-page button ("Recreate catalogs" under the catalog section) or a POST admin endpoint (RequiresElevation, the JF-954.8 pattern: POST /alexaskill/api/catalog/recreate) that nulls the six catalog ids on the user record, backs up the pre-edit config values in the ledger (audit), backdates LastCatalogSync, and triggers the sync — the exact manual sequence, owned by the plugin with guards. (2) RUNBOOK: claudedocs/incident_2026-11-25_catalog-deletion.md — the incident report (timeline, evidence, the unknown deleter with the ruled-out candidates, the diagnosis chain: whole-family cross-locale NONE -> catalog liveness check first), the manual recovery steps as executed (for the case the plugin is down), and the forensic gap (SMAPI offers no deletion audit trail we found; the vendor catalog list shows only AlexaMusic-typed entries; interactionModel catalogs are invisible there — note for future forensics).
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Orchestrator plan (autonomous run, /pa:backlog-orchestrator 2026-11-26):

1. Worker (own worktree, opus tier) implements:
   a. POST /alexaskill/api/catalog/recreate in ConfigurationController (RequiresElevation, the custom-model/* POST pattern): resolve plugin user by userId; capture the six pre-edit catalog ids for audit; null all six (StoredCatalogs-equivalent coverage, reuse CatalogLivenessChecker's map or expose it); LastCatalogSync = null; write an audit ledger row recording the recovery + old ids (JF-966 ledger-writer conventions, one PersistUnderLedgerLock save); clear CatalogIdsDead caveat rows; then queue CatalogSyncTask via Jellyfin's ITaskManager (verify exact namespace from the referenced package; fallback: document why inline trigger is unsafe and queue differently).
   b. config.html: "Recreate catalogs" button in the catalog section (confirm() before POST; inline styles for layout-critical CSS; emby component contract respected).
   c. Unit tests for the endpoint/service logic (id nulling, audit row, caveat clear, unknown user error path).
2. Runbook: claudedocs/incident_2026-11-25_catalog-deletion.md from the incident memory (timeline, evidence, ruled-out deleter candidates, diagnosis chain whole-family-NONE -> liveness first, manual XML recovery steps as executed, forensic gap: SMAPI deletion audit trail absent, AlexaMusic-only /v0/catalogs listing).
3. Gates on merged diff before commit/push: /simplify + code-review high; dotnet build 0 warnings + dotnet test both TFMs; python suites untouched unless model changed (no model change here).
4. DoD items #8 (17 locale strings) and #6 (NLU fixtures) are N/A: admin-only surface, no Alexa speech strings, no interaction-model change.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
ORCHESTRATOR /simplify ROUND (4 parallel angle reviews post-merge, 2026-11-26):

APPLIED: (1) sync-eligibility predicates unified on LibrarySyncService: HasSyncCredentials consumed by the task loop + service entry gates, CanSyncUser (credentials + vendor) as the reporting aggregate the endpoint consumes; closes the code-review-round honesty hole where the hand copy omitted the vendor term (a token+skill-but-no-vendor user was promised recreation while every pass skips them). The task loop deliberately does NOT absorb the vendor term: a vendor-less user must still reach the 12h skip branch where the JF-966 liveness piggyback lives. (2) OtherUsersWithStoredIds folded into CatalogRecoveryOutcome (engine-owned multi-user honesty); controller-side Configuration.Users scan deleted; HasStoredCatalogIds doc'd engine-internal. (3) Server message neutralized (no config-page-button phrasing in API text; the UI explanation stays in config.html). (4) Test scaffolding: liveness suite CreateUser/SeedLedger widened internal (the AttachSmapi precedent), recovery + controller test files delegate; CreateUserWithNoIds factory replaces the two six-null spellings; Payload<T>/AssertSuccess helpers replace six deserialize incantations + three status-assert copies. (5) config.html single-button flex wrapper dropped (margin on the button, the testConnectionButton convention). (6) Joined nulled-ids string hoisted (one join feeds audit + log); log line carries the other-users count. (7) Doc comments name the deliberate sweep-shell divergence between RecreateStoredCatalogs and WriteLedgerCaveat on both sides.

SKIPPED (reasons): FindAll double-alloc on the probe skip path (efficiency angle) - the simplification angle's counter-analysis holds (the filtered-list early exit is load-bearing, folding needs a flag, not simpler; one list per 12h pass is noise); dead-caveat single-pass count fold - the sweep gate precedes the loop by construction, two cheap passes over <=17 rows on a click path; confirm() to modal - the task spec mandates confirm(); pre-existing third carry copy in LibrarySyncService + JObject.Parse conventions - outside this diff.
<!-- SECTION:NOTES:END -->

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

---
id: JF-967
title: >-
  One-click catalog recovery (admin action + incident report/runbook): the
  manual XML-surgery path from the deletion incident must not be the recovery of
  record
status: To Do
assignee: []
created_date: '2026-11-25 15:13'
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

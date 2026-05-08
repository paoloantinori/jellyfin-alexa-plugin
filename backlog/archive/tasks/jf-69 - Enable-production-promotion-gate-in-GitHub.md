---
id: JF-69
title: Enable production promotion gate in GitHub
status: To Do
assignee: []
created_date: '2026-05-04 18:52'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create a production environment in GitHub repo Settings > Environments with required reviewers to enable a promotion gate for production deployments. This ensures that releases cannot be deployed to production without explicit approval from designated reviewers.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A `production` environment exists in GitHub repo Settings > Environments
- [ ] #2 Required reviewers are configured for the production environment
- [ ] #3 CI/CD workflow references the production environment so the gate is enforced on deploy
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

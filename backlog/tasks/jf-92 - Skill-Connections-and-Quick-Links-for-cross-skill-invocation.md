---
id: JF-92
title: Skill Connections and Quick Links for cross-skill invocation
status: Done
assignee: []
created_date: '2026-05-06 19:22'
updated_date: '2026-05-07 01:07'
labels:
  - new-feature
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Enable Skill Connections and Quick Links so that the Jellyfin skill can be invoked from other skills or URL-based deep links. Example: "Alexa, ask Jellyfin to play my favorites" triggered from a web link or another skill.

This is a low-priority polish feature that improves discoverability and cross-platform integration.

Files: Skill manifest, potentially new endpoint or handler for connection requests.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 4.4
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Skill manifest updated with Skill Connections / Quick Links configuration
- [ ] #2 Deep linking works: URL triggers 'Alexa, ask Jellyfin to [action]'
- [ ] #3 Cross-skill invocation tested with a sample caller skill
- [ ] #4 Documentation added for supported Quick Link actions
- [ ] #5 Unit tests for link URL generation
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

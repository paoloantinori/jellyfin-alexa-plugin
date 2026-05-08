---
id: JF-97
title: Fix FallbackIntentHandler dispatch bug breaking all built-in intents
status: Done
assignee: []
created_date: '2026-05-08 18:42'
labels:
  - bug
  - handler-dispatch
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FallbackIntentHandler was registered before YesIntentHandler (alphabetical: F < Y) and its catch-all `AMAZON.*` CanHandle check stole ALL built-in intents (Yes/No/Pause/Resume/Next/Previous/Shuffle/StartOver). Fixed by removing the catch-all and ensuring FallbackIntentHandler is registered last. Deployed to server. Need to verify with real device.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

---
id: JF-402
title: >-
  AMAZON.StopIntent carries 6 custom it-IT samples ('ferma...') - undocumented
  exception to anti-pattern #5, verify-then-remove-or-document
status: To Do
assignee: []
created_date: '2026-08-23 05:57'
labels:
  - interaction-model
  - documentation
  - italian
milestone: m-17
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Model hygiene finding (2026-08-23 audit). AMAZON.StopIntent in model_it-IT.json carries 6 custom samples ("ferma", "ferma tutto", "ferma la musica", "ferma riproduzione", "ferma la riproduzione", "stop"), from the it-IT YAML template lines 50-54. This is the ONLY built-in intent with custom samples in any locale and formally contradicts the repo's own anti-pattern #5 ("custom samples on built-in intents break built-in behavior"). History: added in the early May-2026 template era, evidently deliberate so the Italian imperative "ferma" routes one-shot, and no incident is documented against it.

Action: either (a) verify on-device/profile-nlu that "ferma" routes without the custom samples (Alexa's it-IT built-in may already cover it) and remove them, or (b) keep them and document the exception explicitly in CLAUDE.md anti-pattern #5 and the template YAML so a future cleanup does not "fix" it blindly. Do NOT remove without the on-device check.
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

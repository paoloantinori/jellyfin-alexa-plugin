---
id: JF-444
title: >-
  CancelWords covers only it/en of 17 locales: bare localized cancel words
  during open elicits loop the elicitation trap in 11 locales (locale-keyed
  vocabulary + profile-nlu vetting)
status: To Do
assignee: []
created_date: '2026-09-01 22:27'
labels:
  - code-review
  - i18n
  - dialog
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Util/CancelWords.cs:27'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FindSongIntentHandler.cs:178'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Multiple review findings (JF-423 angles, 2026-09-02): the cancel-word escape hatch vocabulary is a single hardcoded English+Italian HashSet. In 11 of 17 flow-reachable locales (de-DE, fr-FR/fr-CA, es-ES/es-MX/es-US, ja-JP, ar-SA, hi-IN, nl-NL, pt-BR) a bare localized cancel word ('stopp', 'arrête', 'alto'...) captured during an open elicit is searched as a title, matches nothing, and re-prompts - the unbounded elicitation-trap loop the hatch exists to close, burning a full library search per turn (FindSong re-elicits with no repetition bound). The JF-423 code comment documents the gap; this task gives it an owner. The vetting source is the repo's own standard tool (profile-nlu probes of what routes to AMAZON.StopIntent/CancelIntent per locale), not translated guesses.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Structure: CancelWords becomes locale-keyed (per-locale HashSet behind IsCancelWord(string?, string locale) - the LocalizedMoodMap pattern); all hatch call sites already have locale in scope
- [ ] #2 Vetting source: per-locale probes via ask smapi profile-nlu of which bare utterances route to AMAZON.StopIntent/CancelIntent on the deployed model (the repo's own standard tool) - NOT translated guesses; probe a candidate set per locale (2-4 common imperatives each) and only add what actually routes
- [ ] #3 All 17 locales covered or explicitly excluded with probe evidence (e.g. locales whose stop words never route to built-ins via profile-nlu)
- [ ] #4 Unit tests per added locale; the JF-423 tests stay green
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->

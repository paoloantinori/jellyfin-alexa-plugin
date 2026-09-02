---
id: JF-444
title: >-
  CancelWords covers only it/en of 17 locales: bare localized cancel words
  during open elicits loop the elicitation trap in 11 locales (locale-keyed
  vocabulary + profile-nlu vetting)
status: Done
assignee: []
created_date: '2026-09-01 22:27'
updated_date: '2026-09-02 22:22'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped as locale-keyed vocabulary behind IsCancelWord(word, locale) / AnySlotIsCancelWord(request, locale), with all three hatch call sites (PlaySong, PlayAlbum, FindSong) passing the request locale. Every word is probe-vetted against the DEPLOYED model via ask smapi profile-nlu (two rounds per candidate, NO_SELECTION re-probed and confirmed stable): de (stopp/abbrechen/beende/stop), fr-FR+fr-CA shared (arrête/stoppe/annule/stop), es-ES/es-MX (para/cancela/detén/stop), es-US own set (detén does not route there), pt-BR (para/pare/cancela/stop), nl (stop/stoppen/annuleer), hi (रोको/बंद करो/stop + cancel, added in the gate round), ar (إيقاف/stop), en-* shared (stop/cancel, probed en-US/en-GB with documented inheritance carve-out), it-IT legacy set kept add-only with provenance. Exclusions with evidence: es-* 'alto', ar 'توقف', it 'fermare'/'arresta' (NO_SELECTION), it 'fermo' routes to ShowMoreIntent (kept, legacy), and 'cancel' in 9 of 10 own-set non-English locales (NO_SELECTION twice each, probed in the gate round after the review caught the unvetted drop). ja-JP excluded (no deployed model, HTTP 400; English fallback; re-vet candidates recorded). The full probe table lives as a comment in CancelWords.cs; CancelWordsTests pins every locale, exclusion, cross-locale leakage, fallback, and trim/case. JF-423 tests re-paired to the locale-keyed contract. Side finding: the live skill deploys 16 of 17 locale models (only ja-JP missing, consistent with the manifest). Commit 9688645f.
<!-- SECTION:FINAL_SUMMARY:END -->

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

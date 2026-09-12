---
id: JF-551
title: >-
  One-shot wrapper episode-addressing untested in the 8 locales with no
  infinitive family (es-ES/MX/US, pt-BR, nl-NL, ar-SA, hi-IN, ja-JP): probe,
  then add families where the wrapper misroutes
status: To Do
assignee: []
created_date: '2026-09-12 16:55'
labels:
  - interaction-model
  - nlu
  - coverage
dependencies: []
references:
  - JF-549
  - JF-513
  - scripts/validate_interaction_models.py
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-549 /simplify altitude review (F4, 2026-09-12). JF-549 added the series-first one-shot (infinitive) word order to the 9 locales that already HAVE a one-shot carrier family (it-IT, en-US/AU/CA/GB/IN, de-DE, fr-FR/fr-CA). The other 8 (es-ES, es-MX, es-US, pt-BR, nl-NL, ar-SA, hi-IN, ja-JP) have NO one-shot family at all, so their wrapper routing is UNTESTED: the it-IT live evidence (2026-09-12) showed the wrapper form can MISROUTE outright (PlayByGenreIntent), which the JF-549 handler-side series_name elicit cannot catch (the request never reaches PlayEpisodeIntent).

Work: for each of the 8 locales, probe the natural one-shot wrapper form via profile-nlu (e.g. es "reproducir Breaking Bad temporada uno episodio tres" as the wrapper presents it, pt "tocar", nl "afspelen", the ja/ar/hi native wrapper shapes per the locale's invocation conventions - check how each locale's OTHER intents express the one-shot form; ja-JP/hi-IN/ar-SA models glue slots differently, JF-513 has the notes). If the probe misroutes or drops series_name, add the family (both word orders, mirroring the imperative vocabulary the locale already uses). Related residual (JF-549 note F3): the validator's PLAY_EPISODE_ONESHOT_PREFIXES table silently narrows if a locale's carrier vocabulary changes; when extending it for these locales, consider deriving it-IT's entry from the template's infinitive vocabulary or pinning the table in CLAUDE.md like the JF-460 NOUNS table.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 For each of the 8 locales, a profile-nlu probe of the natural one-shot wrapper form for episode addressing selects PlayEpisodeIntent (not a sibling) with series_name filled, or the locale gains the infinitive/one-shot sample family in its template (both word orders) and the probe then passes
- [ ] #2 If families are added: templates regenerate cleanly, the validator Phase 7 table is extended (or the table is derived from template vocabulary), NLU fixtures pin the new forms
- [ ] #3 The decision per locale (probe-passes-as-is vs family-added) is recorded in the task notes
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

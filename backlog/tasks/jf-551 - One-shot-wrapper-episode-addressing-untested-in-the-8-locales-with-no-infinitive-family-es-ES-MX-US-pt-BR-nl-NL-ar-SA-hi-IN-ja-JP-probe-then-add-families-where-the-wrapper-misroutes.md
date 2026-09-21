---
id: JF-551
title: >-
  One-shot wrapper episode-addressing untested in the 8 locales with no
  infinitive family (es-ES/MX/US, pt-BR, nl-NL, ar-SA, hi-IN, ja-JP): probe,
  then add families where the wrapper misroutes
status: In Progress
assignee: []
created_date: '2026-09-12 16:55'
updated_date: '2026-09-21 20:59'
labels:
  - interaction-model
  - nlu
  - coverage
dependencies: []
references:
  - JF-557
  - tests/integration/smapi_client.py
  - .claude/skills/locale-routing-probe/SKILL.md
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PROBE RESULTS 2026-09-13 (the AC#1 probe half is COMPLETE; recorded here because the findings reframe the task): MODEL-SIDE - nl-NL/ar-SA/hi-IN PASS (the natural bare imperative series-first form routes PlayEpisodeIntent with series_name='breaking bad' filled); es-ES/es-MX NO-SELECT on series-first while the series-LAST form routes+fills (order-dependent selection quality, sample family already present - not fixable by adding the infinitive twin: a 'reproducir {series} temporada...' series-first addition was live-tested on the deployed models and FAILED to route cleanly (Fallback on es-ES/es-MX, polluted series='reproducir breaking bad' on es-US) and was REVERTED); es-US and pt-BR steal BOTH episode orders to PlayNextIntent (queue bare-carrier competition); ja-JP steals both orders to PlayVideoIntent. INVOCATION-SIDE (the bigger finding): the one-shot wrapper layer is BROKEN in all 8 locales with the English invocation name 'jellyfin player' - even favorites-payload controls (verbatim samples) fail in es-ES/es-MX/es-US/pt-BR/hi-IN/ar-SA via simulate-skill, de-DE 'sag X ...' worked once of four (unreliable), nl-NL/ja-JP invocations reach the skill but wrapped payload routing is unstable. it-IT works consistently BECAUSE its invocation name is native ('mia collezione'). The JF-511/512 smoke for the 5 new locales used the two-step open-verb convention ('abre jellyfin player' + bare in-session command), never the one-shot wrapper - which is why it passed. CONSEQUENCE: adding infinitive sample families (the original fix shape) cannot fix the one-shot in these locales while the invocation layer fails first. The REAL fix is per-locale localized invocation names (it-IT precedent; the plumbing exists: Config.LocaleInvocationNames) - a product/naming decision for Paolo per locale, THEN the payload families become testable. Verified compositions recorded in SmapiClient.INVOCATION_PREFIX + the locale-routing-probe skill.

2026-09-19: dependency made explicit - the remaining model-side fixes (es-US/pt-BR PlayNext competition, ja-JP PlayVideo steal) and any payload-family work are gated on JF-558 (per-locale localized invocation names, PARKED on Paolo's naming decision 2026-09-18). The probe evidence shows the one-shot invocation layer fails FIRST in all 8 locales with the English name, so model surgery now would be unverifiable end-to-end and risk the es-family-revert class again. Do not pick this task before JF-558 is decided.

2026-09-21 evening matrix (profile-nlu, post-rename): es-ES/MX series-LAST routes perfectly in-session; es-US stolen by PlaySong/PlayNext; pt-BR both orders stolen by PlayNext; ja stolen by PlayByGenre/PlaySong; nl routes (episode_number word-form quirk). Simulator broke for es mid-day (morning-passing open-verb now fails; it-IT control passes). DECISION honoring the 09-13 revert lesson: no model surgery while the wrapper layer is unverifiable. Fix candidates recorded: es SUBJUNCTIVE wrapped form (the morphology the pide-a-X-que wrapper presents; the 09-13 infinitive attempt was wrong morphology), pt/es-US PlayNext carrier qualification or PlayEpisode anchor strengthening, ja anchor probing, nl number vocabulary. GATE to resume: healthy es/de simulate window (poll with the JF-558 battery) or device time.

2026-09-21: JF-558 DECIDED AND DEPLOYED - the gate is LIFTED for es/ja. Native names live (Paolo confirmed). Pure open-verb probe PASSES in simulate for es-ES, es-MX, ja (previously broken with the English name - invocation layer FIXED); their model-side work (payload families, ja PlayVideo steal) is end-to-end testable via simulate. pt-BR/nl-NL/hi-IN/ar-SA remain simulate-BLIND (open probe fails with BOTH names - the documented simulator-outage class, A/B verified 2026-09-21): model-side work lands model-verified (profile-nlu), invocation needs device probes. es-US not probed this round.
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

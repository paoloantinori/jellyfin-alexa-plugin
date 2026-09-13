---
id: JF-551
title: >-
  One-shot wrapper episode-addressing untested in the 8 locales with no
  infinitive family (es-ES/MX/US, pt-BR, nl-NL, ar-SA, hi-IN, ja-JP): probe,
  then add families where the wrapper misroutes
status: To Do
assignee: []
created_date: '2026-09-12 16:55'
updated_date: '2026-09-13 13:26'
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
<!-- SECTION:NOTES:END -->

## Implementation Notes

MODEL-SIDE RESIDUAL CAMPAIGN 2026-09-13 (post-bisect, 10-probe/6-probe batteries):
- pt-BR + es-US (PlayNext steal, song='breaking bad' absorbing the series while dropping the numbers): STABLE loss - an identical-content rebuild did NOT flip it (0/6 post-rebuild, unlike the de-DE JF-553 flip). The winning queue carriers are NOT bare ("tocar {song} depois" / "Reproduce {song} a continuación" - legit trailing-adverb forms), so the JF-459 trim does not apply; removing them would break queue UX. Recorded as platform NLU free-text competition, no model change.
- ja-JP (PlayVideo steal): the BARE '{title} を再生して' carrier was TRIMMED (condemned: stole both episode orders 2/2, JF-459 class) and the model rebuilt SUCCEEDED - but the steal PERSISTS via generalization of the remaining video carriers ('ビデオ {title} を再生して' et al. absorb 'breaking bad のシーズン 1 エピソード 3' whole into title). The trim stays as harm-reduction (the condemned sample is gone; anchored video carriers remain); episode routing in ja needs a deeper carrier redesign, not a trim. NOT further pursued: with the JF-553 nondeterminism finding, per-locale NLU surgery has unstable measurement foundations.
- VALIDATOR REFINEMENT shipped alongside: check #6 (SearchQuery coexistence) is now SAMPLE-level error + intent-level warning - SMAPI accepted BrowseLibrary's filter+browse_category intent shape in every build today (JF-550/JF-557 batches, 17/17 SUCCEEDED), so the old intent-level error was stricter than the platform; the sample-level combination remains the enforced shape. Negative-tested.

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

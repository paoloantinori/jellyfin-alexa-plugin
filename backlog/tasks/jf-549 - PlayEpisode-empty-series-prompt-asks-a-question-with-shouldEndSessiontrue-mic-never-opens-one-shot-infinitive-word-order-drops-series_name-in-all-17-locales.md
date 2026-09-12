---
id: JF-549
title: >-
  PlayEpisode empty-series prompt asks a question with shouldEndSession=true
  (mic never opens); one-shot infinitive word order drops series_name in all 17
  locales
status: In Progress
assignee: []
created_date: '2026-09-12 16:13'
updated_date: '2026-09-12 17:17'
labels:
  - bug
  - video
  - interaction-model
  - nlu
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayEpisodeIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
  - claudedocs/research_alexa-videoapp-stop-routing_2026-09-07.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live device incident 2026-09-12 17:45-17:46 (user report A1). User said "Alexa, chiedi a mia collezione di riprodurre Adolescence stagione 1 episodio 2"; Alexa asked "Non ho capito il nome della serie. Quale serie vorresti guardare?" but the session was CLOSED, so the spoken answer went nowhere (user retried twice, identical shape). Two stacked causes, both verified:

CAUSE 1 (handler, the dead-mic): PlayEpisodeIntentHandler.cs:75-78 returns ResponseBuilder.Tell(DidNotCatchSeriesName) for an empty series_name. Response body logged live: {"outputSpeech":{"text":"Non ho capito il nome della serie. Quale serie vorresti guardare?"},"shouldEndSession":true}. A question shipped with shouldEndSession=true never listens. Fix: Dialog.ElicitSlot on series_name (Ask shape: ShouldEndSession=false + reprompt). This requires PlayEpisodeIntent in the model's dialog.intents in ALL 17 locales or the directive is silently dropped (repo gotcha #9; FindSongIntent already follows this pattern).

CAUSE 2 (model word-order gap): profile-nlu evidence (it-IT, 2026-09-12): "riproduci Adolescence stagione uno episodio due" (imperative, series-first) fills series_name=Adolescence via the catalog-backed SeriesName type. "riprodurre Adolescence stagione uno episodio due" (infinitive, series-first) selects PlayEpisodeIntent but leaves series_name EMPTY. "di riprodurre Adolescence stagione uno episodio due" (the one-shot wrapper form the device actually sends) misroutes to PlayByGenreIntent. The templates only have infinitive samples with the series LAST ("Di riprodurre la stagione {season_number} episodio {episode_number} di {series_name}"); the series-FIRST infinitive order is missing. Fix in all 17 templates: add the infinitive series-first sample family (it-IT via the template's infinitive vocabulary product; other locales per their PlayEpisode word orders), regenerate, validate.

Evidence: journal 2026-09-12T17:45:26/17:45:45/17:46:09 on minix (corr f554563c, 8044722d, f7f78755), request bodies show season_number=1 episode_number=2 resolved ER_SUCCESS_MATCH, series_name absent.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Handler unit test: PlayEpisodeIntent with empty series_name returns Dialog.ElicitSlot targeting series_name with ShouldEndSession=false and a reprompt (never a Tell-shaped question)
- [ ] #2 it-IT generated model contains infinitive series-first PlayEpisodeIntent samples (e.g. 'Di riprodurre {series_name} stagione {season_number} episodio {episode_number}') and PlayEpisodeIntent is registered in the model's dialog.intents
- [ ] #3 profile-nlu (it-IT, after deploy): 'di riprodurre Adolescence stagione uno episodio due' selects PlayEpisodeIntent with series_name resolved to Adolescence
- [ ] #4 No regression: imperative series-first form still routes with series_name resolved (profile-nlu)
- [ ] #5 9 locale templates (it-IT + en-US/AU/CA/GB/IN + de-DE + fr-FR/fr-CA, the locales that HAVE a one-shot carrier family) carry the series-first word-order fix; the 8 family-less locales (es-ES/es-MX/es-US, pt-BR, nl-NL, ar-SA, hi-IN, ja-JP) are explicitly deferred to the follow-up task (their one-shot wrapper routing is untested; the handler elicit covers only the empty-slot shape, not the misroute shape) - JF-550/JF-551 track both residuals; validate_interaction_models.py passes
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
SCOPE AMENDMENT (2026-09-12, /simplify altitude review F4): the original description said 'all 17 templates'; the implemented fix covers the 9 locales that HAVE a one-shot/infinitive carrier family (adding a family to the other 8 is new per-locale authoring, not this bug's fix - see the follow-up task). The 8 family-less locales' one-shot wrapper routing is UNTESTED: the it-IT live evidence showed the wrapper form can MISROUTE to PlayByGenreIntent (never reaching PlayEpisode), which the handler elicit cannot catch. Their residual is tracked in the follow-up.

Altitude review also flagged (F3): the validator's PLAY_EPISODE_ONESHOT_PREFIXES table is a second owner of carrier vocabulary; if a locale's carrier vocabulary changes the table silently narrows. Deeper step when next touched: derive the it-IT entry from the template's infinitive vocabulary (validator Phase 5 already loads templates) or pin it in CLAUDE.md like the JF-460 NOUNS table.

IMPLEMENTED 2026-09-12 (pre-deploy): handler elicit + cancel hatch (reuses FindSongCancelled as the 5th handler already does; BuildDialogElicitResponse shared builder; AllSlotNames hoisted for CA1861 since these slots have no IntentNames.Slots constants), 9 templates + regenerated models, 4 handler tests (13/13 green in the file), NLU fixtures it-IT+en-US, e2e_it-IT shape pin (Dialog.ElicitSlot + open session + reprompt), validator Phase 7 lint (negative-tested on both regression shapes), VOICE_COMMANDS mirrors regenerated via generate_voice_reference.py. /simplify gate: 4 angles run; applied 1 reuse fix (TestHelpers.AssertSessionOpen), 6 simplification fixes (narrative single-homed in the handler, 9 template comments to one line, fixture comment trims, named _series_first predicate, collapsed lint branches, plain-string prefixes, house-shaped test extraction); efficiency clean; altitude clean in-diff with residuals filed as JF-550 (dead-mic sweep, 10 sites) and JF-551 (8-locale one-shot probe). Full suite: net9.0 3666/3666 + net10.0 3666/3666. code-review high gate: dispatched after /simplify. DEPLOY DEFERRED until Paolo's device battery completes (DLL swap restarts Jellyfin; 9 model PUTs take minutes).

CODE-REVIEW HIGH (2026-09-12, dispatched gate): APPROVED with one post-deploy verification residual. All 17 models DO register PlayEpisodeIntent in dialog.intents (the #9 hard requirement, verified mechanically), BUT 6 locales (en-US, ar-SA, hi-IN, ja-JP, nl-NL, pt-BR) declare elicitationRequired: true on all three PlayEpisode slots with prompts.elicitation refs to Elicit.SeriesName/SeasonNumber/EpisodeNumber ids that do not exist in the (empty) prompts array - PlayEpisodeIntent is the ONLY intent in the repo with this shape (every other eliciting intent is false, matching anti-pattern #9's documented manual-dialog shape). Pre-existing (this diff touches samples only), SMAPI builds it (ja-JP built with this shape in the JF-513 redeploy), but the manual-elicit-on-elicitationRequired:true regime has ZERO live evidence and #9-class failures are SILENT. POST-DEPLOY: probe the elicit on one of the 6 (en-US simulate-skill response validation or device), or normalize those 6 templates' dialog blocks to elicitationRequired:false + drop the dangling prompt refs. Neither JF-550 nor JF-551 covers this.

CODE-REVIEW addendum 2: the new NLU fixture rows are PREDICTIONS against the not-yet-deployed model: the it-IT bare-infinitive row ('riprodurre breaking bad stagione uno episodio tre') has no matching sample (the model carries only 'Di ...' and imperative forms) and pre-fix profile-nlu evidence showed the bare form selects the intent but DROPPED series_name; whether it fills now is untested. Treat a red on these rows at the post-deploy NLU run as data (the AC #3/#4 verification), not fixture noise to silently relax.
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

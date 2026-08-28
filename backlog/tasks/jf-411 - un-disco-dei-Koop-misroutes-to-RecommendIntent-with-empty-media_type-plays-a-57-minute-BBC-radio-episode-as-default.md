---
id: JF-411
title: >-
  "un disco dei Koop" misroutes to RecommendIntent with empty media_type, plays
  a 57-minute BBC radio episode as default
status: In Progress
assignee:
  - zai
created_date: '2026-08-28 15:38'
updated_date: '2026-08-28 16:46'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-08-28 15:53:54 (it-IT): user's request (intended "un disco dei Koop", exact ASR form unknown since Alexa sends no raw text) routed to RecommendIntent with the media_type slot EMPTY. The handler default played "Machado de Assis" (BBC Radio 4 "In Our Time", 56m55s audio item), which the user had to pause. Same minute also shows an AMAZON.FallbackIntent at 15:53:32 ("Non ho capito") for another attempt, and the successful misfire to "O" is tracked separately (fuzzy-guard task).

Two coupled problems: (a) NLU competition: RecommendIntent samples are greedy enough to capture an album/artist phrase when other intents miss; (b) empty-slot default behavior: a slotless Recommend plays arbitrary recent library content (a 57-minute radio episode is a poor default for music phrasing). Related platform note (no plugin fix, document only): the user's first "chiedi a mia collezione cosa stiamo ascoltando" attempt never reached the endpoint (no log trace; only the second attempt at 17:08:11 arrived and was served correctly in 163ms) - invocation-level routing upstream, consistent with documented skill competition.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduced or refuted via ask smapi profile-nlu against the it-IT dev model: the exact spoken forms are unknown (Alexa does not send raw utterance text), so test plausible ASR forms of 'un disco dei Koop' ('un disco dei cop', 'un disco dei cup', 'un disco di coop') and record which route to RecommendIntent
- [ ] #2 Decision implemented: either RecommendIntent's it-IT samples are narrowed so artist/album queries stop matching it (preferred, model-side fix), or the handler stops defaulting to library content when media_type is empty and instead prompts for what to recommend
- [ ] #3 NLU fixtures updated (tests/integration/fixtures/it-IT.yaml) covering the routing regression
- [ ] #4 run_nlu_tests.sh --dry-run green; full NLU run for it-IT green
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
AC #1 PROBES DONE (ask smapi profile-nlu, it-IT dev model): ALL bare forms route to AMAZON.FallbackIntent as WINNER with PlayAlbumIntent/PlayArtistSongsIntent only 'considered': 'un disco dei koop', 'un disco dei cop', 'un disco dei cup', 'un disco di coop', 'metti un disco dei koop', and even 'un disco di damien rice'. This REPRODUCES the 15:53:32 Fallback ('Non ho capito'). No probed form routed to RecommendIntent (the exact on-device phrase that did is unknowable; Alexa sends no raw utterance). Root model gap: no indefinite album-by-artist samples existed anywhere (en-US only has title+artist forms like 'play the album {album} by {musician}').

MODEL FIX IMPLEMENTED: PlayAlbumIntent it-IT YAML template gained 12 new template lines (un disco/un album di/dei {musician} in bare + imperative + infinitive forms), regenerated to 44 concrete samples (model_it-IT.json diff verified: only PlayAlbumIntent grew; validators PASS, locale check PASS, no cross-locale drift since the form was missing everywhere).

FIXTURES: 4 new it-IT.yaml entries ('un disco dei queen', 'un album di michael jackson', 'Suona un disco dei queen', 'Metti un album di michael jackson' -> PlayAlbumIntent musician). run_nlu_tests.sh --dry-run now green except test_simulator gibberish which tests the DEPLOYED build (fixed by JF-408's predicate extension once deployed; re-verify post-deploy).

Handler-side empty-slot default (AC #2 option b) intentionally NOT changed: 'consigliami qualcosa'/'cosa mi consigli' are legit optional-slot static samples (anti-pattern #1 boundary) that rely on the default playing something; the model-side fix (option a, preferred per AC) removes the misroute path instead.

REMAINING for this task: deploy the model (SMAPI) + full NLU run for it-IT (post-deploy verification), then close.

HANDLER FIX (code-review findings, two agents converged + verified by direct read): the 44 musician-only samples routed into an Ask(ElicitAlbumName) that discarded the artist (loop risk). Implemented: fresh musician-only utterances now resolve one of the artist's albums and play it (GetArtistAlbums via BuildAlbumQuery with artistIds, first album, logged); the album-name elicit remains for (a) both-slots-empty and (b) delegated dialog mid-flow (DialogState IN_PROGRESS, preserves DialogDelegationTests contract). Flow guard restores the album non-null invariant for the rest of the handler.

Tests: HandleAsync_MusicianOnly_PlaysArtistsAlbum (Koop -> Waltz for Koop play directive) and HandleAsync_InteriorContainmentFuzzyMatch_DoesNotAutoPlay (JF-408 gate, co-located). DialogDelegationTests.PlayAlbum_WithPartialSlots_ElicitsRemaining still green via the IN_PROGRESS discriminator.
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

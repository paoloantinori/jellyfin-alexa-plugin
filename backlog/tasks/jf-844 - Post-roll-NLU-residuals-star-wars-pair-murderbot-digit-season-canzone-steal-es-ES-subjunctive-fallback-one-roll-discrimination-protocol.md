---
id: JF-844
title: >-
  Post-roll NLU residuals: star-wars pair, murderbot digit season, canzone
  steal, es-ES subjunctive fallback - one roll-discrimination protocol
status: To Do
assignee: []
created_date: '2026-10-09 01:57'
labels:
  - nlu
  - diagnostics
  - follow-up
milestone: m-18
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-09 ~04:00 by the orchestrator from the post-deploy live battery (the first full battery after the JF-814+JF-823 model roll: 17-locale rebuild + full catalog re-sync with the fourth type).

FOUR residuals, all same-build stable (2-3 probe/test runs each, deterministic within the build), all needing ONE shared diagnostic: the identical-content rebuild roll + re-probe (the nlu_trainer_nondeterminism protocol; the post-fix worker's model roll provides the roll, the re-probe decides content-caused vs roll-caused for each):

1. STAR WARS PAIR (it-IT): "riproduci il film star wars" resolves PlaySongIntent with only title filled (expected PlayVideoIntent song+title... see fixture) and "suona il film star wars" -> PlaySongIntent (expected PlayVideoIntent). No PlaySong/PlayVideo sample changed tonight; the catalog roll is the suspected cause. If still red after the roll: probe the carrier competition (film noun + free-text title between PlaySong/PlayVideo).

2. MURDERBOT DIGIT FORM (it-IT): "mettere la stagione 1 episodio 3 di murderbot" selects PlayEpisodeIntent, episode_number=3 filled, series ER-matched, but season_number EMPTY (the word form "stagione uno episodio tre" fills fine; both profile-nlu probes 2026-10-09 03:50). The e2e pin (digit form) is the red one. If still empty after the roll: sample-alignment bug in the digit delivery of the season-ed family (AMAZON.NUMBER slot; check whether the model's sample alignment treats the digit as out-of-slot filler after "la stagione").

3. CANZONE STEAL (it-IT): "metti una canzone dei pink floyd" now routes PlayArtistSongsIntent with musician="P!nk floyd" (log-verified 03:50:50 and 03:53:49; the JF-690 arbitration then correctly PROMPTS instead of playing because P!nk AND Pink Floyd both resolve - the JF-420 tie), so the e2e's expected AudioPlayer.Play never comes. Expected PlaySongIntent (song carrier). The sibling carriers pass ("suona i pink floyd" IS an artist fixture; "metti il gruppo pink floyd" passes; "una canzone dei soul coughing" passes). Suspect: the fresh JellyfinArtist catalog version boosting artist-intent confidence for "dei X" tails. NOTE the handler side behaved CORRECTLY throughout (the arbitration prompt is the designed answer for the tie); this is a routing-layer finding.

4. ES-ES SUBJUNCTIVE FALLBACK: "que reproduzca breaking bad temporada uno episodio tres" -> AMAZON.FallbackIntent (expected PlayEpisodeIntent). The es one-shot wrapper family (JF-551 reproduzca) + tonight's es season-less additions both rolled. If still Fallback after the roll: the season-less family displaced the subjunctive wrapper's mass; fix by strengthening the wrapper samples.

Also recorded in the same battery (NOT residuals, already dispositioned): the JF-814 competition steal ("l'episodio successivo" -> PlayEpisodeIntent; fix in flight with the post-A/B worker: the NextUp article form) and the JF-823 A/B verdict (see the JF-823 task's LIVE A/B VERDICT section; generic-word fix in flight, out-of-catalog misroute accepted as the JF-684 tradeoff pending the handler-guard question).

PROTOCOL for whoever runs it (post-worker): deploy/roll the models, then re-probe each residual 3x on the new build; green = roll-nondeterminism (record on this task, close); red = content-caused (fix or file per shape with the probe evidence). The e2e simulate per-locale outages (waltz bare-opens in es/fr) are the KNOWN outage class, excluded here.
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

ROLL-DISCRIMINATION VERDICT (2026-10-09 06:50, after the post-A/B roll: 17-locale
rebuild + sync 2 of 16/16 canaries):

1. STAR WARS PAIR: SPLIT. "riproduci il film star wars" -> PlayVideoIntent
   title=star wars GREEN (the roll flipped it: roll-nondeterminism, exactly
   the memory's class). "suona il film star wars" -> PlaySongIntent
   song=star wars STILL RED: content-or-carrier-specific. NOTE the semantic
   angle: "suona" is the it-IT MUSIC verb (JF-402 era); routing a film ask
   on a music verb to PlaySong is defensible NLU. The fixture pins
   PlayVideo; the next round should DECIDE (re-pin to PlaySong with a
   rationale, or strengthen the film-noun carrier) rather than blind-fix.
2. MURDERBOT DIGIT SEASON: CONTENT-CAUSED (stable red across two builds):
   "mettere la stagione 1 episodio 3 di murderbot" -> PlayEpisodeIntent
   episode_number=3 filled, season_number EMPTY; the word form fills. The
   digit delivery after "la stagione" does not slot-align. Fix direction:
   add a digit-friendly sample shape or check the alignment of "la stagione
   {season_number} episodio" against digit input; re-probe both forms.
3. CANZONE STEAL: not re-probed at profile-nlu level this round (the e2e
   simulate would need the outage-free window); the mechanism record stands
   (routes PlayArtistSongsIntent musician='P!nk floyd'; the JF-690
   arbitration prompts on the P!nk/PinkFloyd tie, handler-side blameless).
   Re-probe with the next e2e window.
4. ES-ES SUBJUNCTIVE: CONTENT-CAUSED (stable red across two builds):
   "que reproduzca breaking bad temporada uno episodio tres" ->
   AMAZON.FallbackIntent. The es season-less additions displaced the
   JF-551 subjunctive wrapper's mass. Fix: strengthen the wrapper samples
   (the es-ES PlayEpisode one-shot family) in the same change style as the
   NextUp article round.

GENERIC-WORD POST-FIX VERIFICATION (the related JF-823 item, verified green
in the same window, recorded here for one-stop reading): the real carriers
route PlayBook live - de-DE "spiele hörbuch" / "hör hörbuch" / "spiele das
buch hörbuch", en-GB "play audiobook". The earlier NO_SELECTION probes
("lies ein hörbuch", "read audiobook") used verbs the PlayBook family never
carried in those locales - NOT regressions. Probe-phrase lesson: probe the
model's OWN carriers, not translated guesses.

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

MODEL-RESIDUALS FIX ROUND (2026-10-09 pre-dawn, the fix worker; NO SMAPI in
this window, so every template change below is UNVERIFIED LIVE by
construction: the fixture pins are the verification instruments and the
LIVE PROBE IS THE MORNING'S FIRST ACTION after the orchestrator's roll):

1. MURDERBOT DIGIT SEASON, FIXED TEMPLATE-SIDE, shape: the BARE-INFINITIVE
   numbers-first rows. Chosen over the article-less twin because the red
   form is the wrapper-remainder tail ("mettere la stagione 1 ..." with NO
   "Di" prefix and NO imperative verb): the numbers-first family carried
   only "{imperative} la stagione ..." and "{infinitive} (= Di + verb) la
   stagione ...", so the bare tail matched nothing exactly and the trainer
   aligned it by generalization; intent and episode_number filled, the
   season digit did not (word delivery did). This is the SAME class the
   PlaySong family hit live 2026-09-24 ("the trainer does not generalize
   riproduci->riprodurre for an unanchored shape"), fixed there by explicit
   bare-infinitive rows; four rows added (riprodurre/suonare/mettere/
   ascoltare la stagione {season_number} episodio {episode_number} di
   {series_name}; pleiare stays vocabulary-only per the PlaySong/PlayBook
   precedent). The article-less verbless twin was REJECTED: no live
   evidence anyone says the verbless form, and uncondemned additions shift
   trainer statistics we cannot verify from here (the JF-541 removal-note
   discipline, applied symmetrically to additions). VERIFICATION: the new
   profile-nlu pin pair in it-IT.yaml, digit form "mettere la stagione 1
   episodio 3 di murderbot" plus the word-form contrast twin
   "mettere la stagione uno episodio tre di murderbot" (both pin
   season_number + episode_number; series_name left free, catalog
   wiring-state dependent), plus the standing e2e pin; all marked
   red-until-roll in their comments.

2. ES-ES SUBJUNCTIVE, FIXED TEMPLATE-SIDE, shape: the CONNECTOR-RESIDUE
   subjunctive twins, exactly the path the es-ES fixture note pre-recorded
   ("the connector form needs its own sample, the it-IT 'Di' precedent").
   The red form is "que reproduzca ..." (the wrapper's connector residue),
   which had no sample; after the JF-814 season-less additions displaced
   the family's mass it fell to FallbackIntent. Three rows added, mirroring
   the bare subjunctive family one-to-one: "que reproduzca {series_name}
   temporada ... episodio ...", "que reproduzca la temporada ... de
   {series_name}", and the season-less twin "que reproduzca el episodio
   {episode_number} de {series_name}". No other subjunctive carriers
   invented (vea/ponga are evidence-free; JF-551 chose reproduzca on
   research). VERIFICATION: the standing connector pin in es-ES.yaml
   ("que reproduzca breaking bad temporada uno episodio tres" ->
   PlayEpisodeIntent), now annotated as the red-until-roll instrument and
   STRENGTHENED to pin season_number + episode_number like its bare-form
   sibling (the murderbot class is "intent selected, numbers empty"; a
   series-only pin would read green through it).

3. SUONA-FILM, DECIDED (a): fixture RE-PINNED to PlaySongIntent
   (song pinned presence-only; the title slot pin dropped because PlaySong
   routing fills song, live reading song='star wars'). Rationale: 'suona'
   is the it-IT music-performance verb; the JF-504 film-noun carriers
   DELIBERATELY exclude it (Riproduci/Metti only, "unidiomatic with a film
   object", recorded in the it-IT template's PlayVideo block), the JF-436
   probes show PlayVideo is never selected on Suona, and every natural
   film ask routes PlayVideo green (the roll flipped 'riproduci il film
   star wars'; 'metti il film matrix' and the guardare family pinned).
   No evidence found that the "suona + film noun" phrase class is common;
   strengthening the film carrier would contradict the recorded JF-504
   verb discipline. The pin records the semantic expectation (music verb
   -> music intent, title captured); a future flip flags for triage,
   same convention as the JF-436 pins (record reality, do not endorse).

Canzone steal (#3 of the original four): NOT this round's surface; the
verdict keeps it "re-probe with the next e2e window" (the mechanism record
stands: routes PlayArtistSongsIntent, musician='P!nk floyd', the JF-690
arbitration prompts on the P!nk/PinkFloyd tie, handler-side blameless).

Morning protocol (the orchestrator): roll the 17-locale rebuild + catalog
sync, then re-probe in order: (1) it-IT "mettere la stagione 1 episodio 3
di murderbot" at profile-nlu (the new pin) 3x plus its word-form twin;
green closes item 1, red means the bare-infinitive anchor theory is wrong
(next lever: the series-first bare-infinitive rows); (2) es-ES "que
reproduzca breaking bad temporada uno episodio tres" 3x plus the bare-form
control; (3) the suona re-pin row (expect PlaySongIntent song filled);
(4) the UNCOVERED symmetric shape called out by the code review, probed
before any fix: it-IT "riprodurre breaking bad stagione 1 episodio 3"
(series-first bare-infinitive DIGIT remainder; no sample anchors it, no
pin covers it; if it reproduces the murderbot misalignment, the fix is the
series-first bare rows with the same four-verb shape); then the e2e
windows for the murderbot row and the canzone re-probe.

CROSS-LOCALE FOLLOW-UP FLAGGED BY THE /simplify ALTITUDE REVIEW (recorded
here because the fix worker's surface was bounded to it-IT + es-ES): the
connector residue is a property of the SHARED es one-shot wrapper ("pide a
{inv} que reproduzca ..."), and both parent changes landed it in all three
es locales (JF-551's reproduzca family is in es-ES/es-MX/es-US; so are the
JF-814 season-less additions whose mass displaced it; es-US's own header
and fixture live-observed the identical "que reproduzca" residue glue).
The JF-844 connector rows were landed in es-ES ONLY (the worker's
boundary); es-MX and es-US keep the latent FallbackIntent failure and
carry no connector pin, so their next trainer roll can surface it as an
unexplained red. The morning's es-ES probe doubles as the evidence
trigger: if it goes green, mirror the three "que reproduzca" rows (plus a
connector pin each) into es-MX.yaml and es-US.yaml in the same change
style; if es-ES stays red, solve es-ES first and the siblings inherit the
fix.

MORNING BATTERY VERDICT (2026-10-09 08:15, after the third roll: 17/17
rebuild + sync 16/16 canaries; the JF-844 branch-only round live):

1. SUONA RE-PIN: GREEN (PlaySongIntent song=star wars, exactly the
   re-pinned fixture). Item closed.
2. SERIES-FIRST BARE DIGIT: GREEN ("riprodurre breaking bad stagione 1
   episodio 3" fills season_number=1 episode_number=3; the new
   bare-infinitive rows work for their shape). The digit mechanism itself
   CAN fill; the failure is positional to the stagione-first order.
3. MURDERBOT STAGIONE-FIRST DIGIT: STILL season_number=None 3/3 (stable
   three builds). MITIGATED BY DESIGN though: with episode parsed and
   season empty, the JF-814 elicit branch now ASKS the season instead of
   silently playing next-up, so the UX is correct-by-ask (one extra turn
   for this digit order). Residual accepted unless a sample shape that
   fills stagione-first digits is ever found; JF-843's absolute-numbering
   fix will rework this path anyway.
4. ES-ES SUBJUNCTIVE: STILL FallbackIntent 3/3, AND the bare control
   "reproduce breaking bad temporada uno episodio tres" now routes
   PlayNextIntent song=breaking bad (a music-next steal of a plain episode
   ask - new adjacent breakage, possibly roll-instability). The es-SE
   episode-intent mass is unstable; the day session should run the
   identical-content rebuild protocol on es-ES BEFORE any sample surgery,
   and audit the season-less family's interaction with the es episode
   intents as a set. NOT a night-fixable shape; the fixtures stay
   red-marked.
Standing guards all green in the same battery: the incident phrase, the
article competition, sapiens.

DENSIFICATION VERDICT (2026-10-09 16:40, after the surgery roll: three es
PUTs + wiring sync, canaries green at the new counts 458/455/443):

WON (the shapes the new rows cover): "que reproduzca breaking bad temporada
uno episodio tres" -> PlayEpisodeIntent ALL SLOTS (the ORIGINAL residual,
FallbackIntent across three builds, now green); "ver ..." -> PlayEpisodeIntent
all slots. The season-less controls stay green.

STILL STOLEN: "reproduce/pon breaking bad temporada uno episodio tres" (the
series-first shapes whose exact samples pre-existed) -> PlaySongIntent with
song="breaking bad temporada 1" in es-ES; in es-MX/es-US the same phrase goes
to PlayNextIntent (a different thief; their families are 12 rows vs es-ES 14).

CONCLUSION: densification wins exactly the shapes it adds rows for; the
series-first reproduce/pon shapes remain dominated by the free-text music
carriers regardless of episode-family mass. The remaining lever is the THIEF
side: the PlaySong bare carriers ("reproduce {song}") in the es locales - the
same class as the JF-459 bare-album trim, but the bare song carrier IS the
song-ask design, so trimming or qualifying it trades episode-routing against
song-routing and needs the maintainer's call plus a live A/B. FILED as the
open question below; the es fixtures stay red as the tracker.

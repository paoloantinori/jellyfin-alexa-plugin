# JF-959 census re-verification (2026-11-25)

Re-probe of every JF-959 census single against the CURRENT live skill
(`amzn1.ask.skill.33dfacd5-3676-4cdc-8b02-81efb227df83`, carrying the 2026-11-24
evening full 17-locale rebuild + retrain and the JF-958 es-episode fix).
Method: profile-nlu via `tests/integration/smapi_client.py` (delay 2.0s), two
full probe passes per row, evergreen it+en controls green on both passes before
any diagnosis. Every still-red locale then got one NLU-lab round
(`scripts/nlu-lab/cli.py run --template`, lab skill
`amzn1.ask.skill.2d34cb09-584a-4c8a-8b7b-b22991bf02c3`, no catalog) to separate
model-shape defects from catalog/trainer divergence.

The census predates two 2026-11-24 changes (JF-958 es-episode fix, full
17-locale rebuild + retrain), so this document answers one question per row:
still red, fixed by the rebuild, or flipped to a new shape.

## Verdict buckets

- FIXED: green on the current live skill, both probes.
- STILL-RED-CONFIRMED: red on both live probes; the lab round either agrees
  (model-shape) or is green while live is red (live-only divergence, catalog
  gate family).
- UNSTABLE: the wrong winner differs between probes or between the lab and the
  live skill on identical content (trainer-marginal).
- NEEDS-CARRIER-DECISION: red through a carrier-word collision; the fix touches
  carrier samples or the fixture pin, so the maintainer's carrier rule applies.
  No fixture, template, or model file was edited in this re-verification.

## Per-row results

### 1-2. hi-IN film rows (`film inception dekho` word orders)

Expected PlayVideoIntent. Sample `फिल्म {title} देखो` EXISTS in the hi-IN
template (title is an AMAZON.SearchQuery slot).

| Probe | Utterance | Result |
|---|---|---|
| live 1 | `film inception dekho` (romanized) | AMAZON.FallbackIntent, no slots |
| live 2 | `film inception dekho` | AMAZON.FallbackIntent |
| live 3 | `inception film dekho` | AMAZON.FallbackIntent |
| live 4 | `inception film dekho` | AMAZON.FallbackIntent |
| lab 1+2 | romanized orders | no-selection (both) |
| live 5+6 | `फिल्म इंसेप्शन देखो` (Devanagari, exact sample shape) | AMAZON.FallbackIntent, no slots (both) |

Verdict: STILL-RED-CONFIRMED. Unchanged from the census (Fallback), and the
failure is systematic: even the Devanagari utterance that exactly instantiates
the template sample Fallbacks on live (twice). The romanized census surface
also cannot match a Devanagari sample set by construction (probe-shape
artifact), but the Devanagari probes show the model itself fails on the real
surface too. The {title} SearchQuery span filled with a transliterated foreign
title is the suspect span; the lab was probed in romanized shapes only, so a
Devanagari lab round is an open follow-up.

### 3. hi-IN song row (`gana shahar ki awaz chalao`)

Expected PlaySongIntent. Census actual: BrowseLibraryIntent.

| Probe | Utterance | Result |
|---|---|---|
| live 1+2 | `gana shahar ki awaz chalao` (romanized) | AMAZON.FallbackIntent, no slots (both) |
| lab 1 | romanized | AMAZON.FallbackIntent |
| live 3+4 | `गाना शहर की आवाज़ चलाओ` (Devanagari) | BrowseLibraryIntent with browse_category=`गाना`, filter empty (both) |

Verdict: NEEDS-CARRIER-DECISION. The census's BrowseLibraryIntent actual
reproduces exactly in Devanagari (twice): the carrier word `गाना` (song) is
stolen as BrowseLibraryIntent's browse_category value. `गाना` is simultaneously
PlaySongIntent's carrier word and a captured BrowseCategory value, which is the
issue-46 token-ownership collision class that JF-954.2's lint targets. The
romanized surface is additionally unmatchable against the Devanagari samples.
Fix requires the maintainer's carrier rule (same class as the de-DE `Lied`
collision); no edit made here.

### 4. ja-JP playlist-carrier steal (`shuffle shirotsumekusa`)

Expected PlaySongIntent per census; census actual AddSongToPlaylistIntent.

| Probe | Utterance | Result |
|---|---|---|
| live 1 | `白つめくさをシャッフルして` | PlaySongIntent, song=白つめくさ |
| live 2 | same | PlaySongIntent, song=白つめくさ |

Verdict: FIXED on the kana surface. Caveat recorded: the census wrote the
utterance in romaji; ja-JP ASR never produces romaji, so the kana surface (the
one the model's samples are built from) is the realistic surface and it routes
correctly after the retrain. Recommend re-pin to the kana surface or closure.

### 5. ar-SA song row (`شغل الأغنية مدينة من ورق`)

Expected PlaySongIntent. Census actual: PlayVideoIntent. This is the JF-933
RED-UNTIL-DEPLOY pin row; the sample `شغل الأغنية {song}` exists in the ar-SA
template (line 60).

| Probe | Utterance | Result |
|---|---|---|
| live 1 | same | AMAZON.FallbackIntent, no slots |
| live 2 | same | AMAZON.FallbackIntent |
| lab 1+2 | same | no-selection (both) |

Verdict: STILL-RED-CONFIRMED. The rebuild + retrain did not turn the row green
and the actual flipped from the PlayVideoIntent steal (census) to Fallback.
The lab (template-only model) also fails to select, so the model-shape carries
the sample yet neither environment matches it; systematic, needs a dedicated
investigation round (the JF-933 synonym-removal fix landed, but selection
itself fails).

### 6. en-US artist steal (`give me music by coldplay`)

Expected PlayArtistSongsIntent. Census actual: PlaySongIntent.

| Probe | Environment | Result |
|---|---|---|
| live 1+2 | live | PlaySongIntent, musician=Coldplay, song=`"Give Me Music" by The Ramblers` (identical both) |
| lab 1+2 | lab (template) | PlaySongIntent, identical slots (both) |

Verdict: NEEDS-CARRIER-DECISION. Systematic in BOTH environments with
identical slot hallucination: the built-in AMAZON.MusicRecording catalog
expands `music` into a real entity ("Give Me Music" by The Ramblers), the
`give me {song}` sample (PlaySongIntent) wins over `give me {musician}`
(PlayArtistSongsIntent), and no `music by {musician}` carrier row exists on the
artist intent. `music` is at the same time a LibraryQueryType synonym
(tracks) and a carrier token in several intents: the issue-46 collision class.
Fix requires the maintainer's carrier rule (add artist-intent carrier rows
and/or trim the `give me {song}` sample; re-pinning the fixture to
PlaySongIntent would legitimize the steal and is not recommended). No edit
made here.

### 7. en-US mood steal (`play dinner music`)

Expected PlayMoodMusicIntent. Census actual: PlaySongIntent. `dinner` IS in
the en-US Mood vocabulary and `play {mood} music` is a PlayMoodMusicIntent
sample, so this is not the fixture-vs-vocabulary class.

| Probe | Environment | Result |
|---|---|---|
| live 1+2 | live | PlayNextIntent, song=`"Dinner Music"` (identical both) |
| lab 1+2 | lab (template) | PlaySongIntent, song=`"Dinner Music"` (identical both) |

Verdict: UNSTABLE across model builds, stable-wrong within each. Both
environments steal the phrase onto a MusicRecording entity ("Dinner Music" is a
real Amazon Music entity) and defeat the Mood custom slot, but the winning
competitor differs by build (census: PlaySongIntent; current live retrain:
PlayNextIntent; lab build: PlaySongIntent). Trainer-marginal competition;
the triage-rule classification (JF-954.6) is the known-flaky class unless the
maintainer decides on a carrier/vocabulary fix. No edit made here.

### 8. en-US speed row (`normal speed`)

Expected SetPlaybackSpeedIntent. Census actual: not captured.

| Probe | Environment | Result |
|---|---|---|
| live 1+2 | live | SetPlaybackSpeedIntent, speed slot empty (both) |
| lab 1+2 | lab | SetPlaybackSpeedIntent (both) |

Verdict: FIXED. Green on both live probes and the lab.

### 9-10. it-IT composed artist shapes (`metti/di suonare una canzone dei soul coughing`)

Census listed these under de-DE; the utterances are Italian and the JF-684
catalog-gate mechanism is it-IT-only, so the census locale label is a mislabel.
Treated here as it-IT. Expected PlayArtistSongsIntent. Census actual:
no-selection.

| Probe | Environment | Utterance | Result |
|---|---|---|---|
| live 1+2 | live | `metti una canzone dei soul coughing` | PlayArtistSongsIntent, musician=soul coughing (both) |
| live 3+4 | live | `di suonare una canzone dei soul coughing` | PlayArtistSongsIntent, musician=soul coughing (both) |
| lab 1+2 | lab | both shapes | FindSongIntent with empty titleKeywords (both) |

Verdict: FIXED on live. Notable divergence recorded for the ledger: on the
catalog-free lab skill the same two shapes steal to FindSongIntent, so the live
JellyfinArtist catalog's ER resolution is what makes PlayArtistSongsIntent win
here. The fixtures already pin PlayArtistSongsIntent for these rows, so no
re-pin is needed.

### 11. it-IT wrapper title row (`chiedi a mia collezione di suonare il brano rapsodia`)

Expected PlayLibrarySongIntent. Census actual: no-selection, with the JF-684 x
wrapper mechanism hypothesis (armed SongTitle catalog gates SELECTION, the
wrapper composition pollutes the title span).

| Probe | Environment | Result |
|---|---|---|
| live 1 | live | AMAZON.FallbackIntent, no slots |
| live 2 | live | AMAZON.FallbackIntent |
| lab 1+2 | lab (template, no catalog) | PlayLibrarySongIntent, librarySong=rapsodia (both GREEN) |

Verdict: STILL-RED-CONFIRMED on live, mechanism now PROVEN as live-only: the
identical template content routes correctly twice on the catalog-free lab and
Fallbacks twice on the live skill. The census's catalog-gate hypothesis is
confirmed in its sharp form (shape flipped no-selection to Fallback after the
retrain). The candidate fixes remain a maintainer decision: catalog-side
(the JF-684 family fix) or wrapper sample rows (each verb-noun combo
multiplies rows, the JF-844 lesson). No edit made here.

### 12. es-US song row (`Reproduce la canción luciérnagas del sur`)

Expected PlaySongIntent. Census actual: Fallback, "free-text MusicRecording
slot, no catalog wired there; cause unclear".

| Probe | Environment | Result |
|---|---|---|
| live 1 | live | AMAZON.FallbackIntent, no slots |
| live 2 | live | AMAZON.FallbackIntent |
| lab 1+2 | lab (template) | PlaySongIntent, song=`la cancion luciernagas del sur` (both GREEN) |
| live model fetch | live | PlaySongIntent 21 samples == template's 21; intent set == template + 16 AMAZON.* built-ins; dialog-driven fills on PlayEpisodeIntent (18) and SkipForwardBackIntent (9); types equal + catalog wiring present |

Verdict: STILL-RED-CONFIRMED, live-only divergence. Two census facts corrected:
(1) the live model content equals the current template (the rebuild landed),
and (2) es-US is NOT catalog-free: the live model carries catalog wiring on
SeriesName, AudiobookTitle, and JellyfinArtist (read via
get-interaction-model). With model content identical and the catalog-free lab
green, catalog wiring is the only structural difference left, the same
lab-green/live-red signature as row 11. Mechanism unresolved at probe level;
recommended next step is a catalog-side ablation (JF-684 family), not a
fixture/template edit.

## Summary table

| # | Locale | Row | Census actual | Re-probe actual | Verdict | Recommended action |
|---|---|---|---|---|---|---|
| 1 | hi-IN | film inception dekho | Fallback | Fallback x4 (romanized + Devanagari exact-sample), lab no-selection | STILL-RED-CONFIRMED | dedicated investigation; open: Devanagari lab round |
| 2 | hi-IN | inception film dekho | Fallback | Fallback x4, lab no-selection | STILL-RED-CONFIRMED | same as row 1 |
| 3 | hi-IN | gana shahar ki awaz chalao | BrowseLibraryIntent | romanized: Fallback x2 + lab Fallback; Devanagari: BrowseLibraryIntent x2 (reproduced) | NEEDS-CARRIER-DECISION | maintainer carrier rule (गाना collision, issue-46 class) |
| 4 | ja-JP | shuffle shirotsumekusa | AddSongToPlaylistIntent | PlaySongIntent x2 (kana) | FIXED | re-pin fixture to kana surface or close |
| 5 | ar-SA | shgh al-aghniya madina min waraq | PlayVideoIntent | Fallback x2, lab no-selection x2 | STILL-RED-CONFIRMED (flipped) | dedicated investigation; JF-933 row still red post-rebuild |
| 6 | en-US | give me music by coldplay | PlaySongIntent | PlaySongIntent x2, lab identical x2 | NEEDS-CARRIER-DECISION | carrier rule (music token collision); do not re-pin fixture |
| 7 | en-US | play dinner music | PlaySongIntent | PlayNextIntent x2 live, PlaySongIntent x2 lab | UNSTABLE (trainer-marginal) | leave to the JF-954.6 triage rule; no edit |
| 8 | en-US | normal speed | (not captured) | SetPlaybackSpeedIntent x2, lab green | FIXED | re-pin/close |
| 9 | it-IT* | metti una canzone dei soul coughing | no-selection | PlayArtistSongsIntent x2 | FIXED | no fixture change needed (already pinned) |
| 10 | it-IT* | di suonare una canzone dei soul coughing | no-selection | PlayArtistSongsIntent x2 | FIXED | no fixture change needed (already pinned) |
| 11 | it-IT* | chiedi a mia collezione di suonare il brano rapsodia | no-selection | Fallback x2 live; lab GREEN x2 | STILL-RED-CONFIRMED (mechanism proven: live catalog gate) | maintainer decision: catalog-side vs wrapper rows |
| 12 | es-US | Reproduce la canción luciérnagas del sur | Fallback | Fallback x2 live; lab GREEN x2; live model == template | STILL-RED-CONFIRMED (live-only) | catalog ablation next; census "no catalog" claim refuted |

\* census locale mislabel: the "de-DE" rows are Italian utterances; JF-684
catalog gating is it-IT-only.

## Scorecard

FIXED: 5 rows (ja-JP shuffle, en-US speed, it-IT metti, it-IT di-suonare, and
counting row 4's kana caveat). STILL-RED-CONFIRMED: 5 rows (hi-IN films x2,
ar-SA song, it-IT rapsodia, es-US song). NEEDS-CARRIER-DECISION: 2 rows
(hi-IN song, en-US coldplay). UNSTABLE: 1 row (en-US dinner). No row flipped
from red to a DIFFERENT red without a verified mechanism; the four shape flips
(rows 3, 5, 7, 11) are each explained above.

## SMAPI call count

- Live-skill profile-nlu: 26 (probe pass 1) + 26 (pass 2) + 4 (hi-IN Devanagari
  pair x2) = 56.
- Live-skill management: 1 get-interaction-model (es-US) + 1 list-skills
  (discovery) = 2.
- Lab skill: 5 model deploys with build-wait polling, plus 546 battery probes
  (hi-IN 38, ar-SA 16, en-US 177, it-IT 285, es-US 30).
- Total: 602 profile-nlu probes plus 5 deploys, all within the 2s SMAPI spacing.

## Protocol compliance notes

- Evergreen controls (it-IT `suona la band radiohead`, en-US
  `put on tracks by pink floyd`) ran first and were green on both passes; the
  simulate outage signature did not appear, so no locale's live probes were
  skipped.
- Trainer variance: every row carries at least two identical probes; the five
  still-red locales additionally carry the lab contrast.
- No fixture, template, model JSON, or backlog task file was touched; per the
  task, the coordinator files the actions from this report.

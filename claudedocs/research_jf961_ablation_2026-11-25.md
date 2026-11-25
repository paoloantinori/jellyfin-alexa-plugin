# JF-961: catalog-wiring ablation on the live skill's es-US model (2026-11-25)

Question: is the JF-684 catalog-selection gate (a catalog-backed slot type whose values carry no match for the spoken phrase discourages intent selection) MODEL-WIDE (any unmatched catalog type in the model vetoes selection for intents that never consume that type), PER-INTENT (only intents consuming the wired type suffer), or is the es-US red row actually a TOKEN COLLISION with catalog values?

Live skill under test: `amzn1.ask.skill.33dfacd5-3676-4cdc-8b02-81efb227df83` (development stage), locale es-US ONLY. The red row under investigation is census row 12 of the JF-959 re-verification: `Reproduce la cancion luciernagas del sur` reads FallbackIntent on live x2 and PlaySongIntent on the catalog-free lab x2, with the live model content verified equal to the committed template. The live es-US model carries catalog wiring on exactly three types:

| Type | Catalog id (tail) | Pinned version |
|---|---|---|
| SeriesName | ...8b8a094a2499 | 1298 |
| AudiobookTitle | ...6a9c2982af4a | 608 |
| JellyfinArtist | ...48345ae718f3 | 1863 (musician slots re-typed AMAZON.Musician to JellyfinArtist) |

## VERDICT (one paragraph)

Neither simple mechanism holds. The censused red row is a two-factor CONJUNCTION effect: it reproduces ONLY when the JellyfinArtist wiring (the catalog type PlaySongIntent consumes through its own musician slot) is present TOGETHER WITH at least one more catalog wiring elsewhere in the model. Every single-wiring state is green on the carrier row (audiobook-only, series-only, synthetic-audiobook-only, artist-only), the audio+series pair without artist is green, and both pairs containing artist plus the full triple are red (Fallback, reproducible x2 in every cell). The simple model-wide reading is refuted (series-only and audio+series green), the token-collision reading is refuted for the censused row (the real audiobook values alone leave it green, and pseudoword values change nothing there), and the single-type per-intent reading is refuted (artist-only green). A secondary, cleaner token-collision finding exists on the NAKED-carrier form only: the real audiobook values break the naked row where pseudoword values do not, proving value-content (token-level) causation for that shape.

## Design (Fable's five arms, extended to nine wiring states)

Every arm model is built from the SAME base with only the wiring axis changed. The base is the committed template content (`model_es-US.json`); the reversal of the live model's wiring onto that base was machine-verified canonically identical to the committed template (static SeriesName/AudiobookTitle seeds restored, musician slots re-typed to AMAZON.Musician, JellyfinArtist type removed). Deploy via `ask smapi set-interaction-model` (locale es-US), build wait via get-skill-status (lastUpdateRequest.status SUCCEEDED with a post-deploy delay closing the stale-SUCCEEDED window), wiring-state readback via get-interaction-model after every rebuild, then the probe battery via profile-nlu at 2s spacing. Two identical rebuilds per arm (trainer-variance control); every cell's two passes came out row-for-row identical.

| Arm | Wiring set | Status |
|---|---|---|
| 1 | none (template-equivalent) | designed |
| 2 | AudiobookTitle (real 608) | designed |
| 3 | SeriesName (real 1298) | designed |
| 4 | AudiobookTitle -> SYNTHETIC catalog (234 pseudowords) | designed |
| 5 | all three (= pre-experiment live state) | designed |
| 6 | JellyfinArtist only (real 1863) | added: direct test of the by-elimination suspect after arm 5 |
| 7 | AudiobookTitle + SeriesName | added: pair threshold map for JF-960 |
| 8 | AudiobookTitle + JellyfinArtist | added |
| 9 | SeriesName + JellyfinArtist | added |

Arm 4 hosting note: the SMAPI interaction-model catalog flow only accepts a hosted source URL (the in-band upload flow 404s; it targets the AlexaMusic `/v1/catalogs` namespace). The source URL was a temporary secret gist (commit-pinned raw URL, anonymous-fetch verified, DELETED after the experiment; flagged for coordinator review since it is an outward artifact on the maintainer's GitHub). Catalog `amzn1.ask.interactionModel.catalog.350015e1-d70e-498d-9897-c32d9bc0d3c3` version 1 was created from it (SUCCEEDED, totalCount 234 verified). Values are deterministic pseudowords (seed 961), 234 entries (same size as the real audiobook catalog), with machine-verified zero token overlap against the battery and against every real audiobook title token. Amazon accepted model builds referencing the new catalog with no explicit associate call.

## Battery (9 rows, run twice per arm, once after each identical rebuild)

| Row | Utterance | Expectation |
|---|---|---|
| red_reproduce_acc | Reproduce la canción luciérnagas del sur | PlaySongIntent (census surface) |
| red_reproduce_plain | Reproduce la cancion luciernagas del sur | PlaySongIntent (accentless twin) |
| red_escucha | Escucha la cancion luciernagas del sur | PlaySongIntent (verb variant) |
| red_pon | Pon la cancion luciernagas del sur | PlaySongIntent (verb variant) |
| red_toca | Toca la cancion luciernagas del sur | PlaySongIntent (verb variant) |
| red_bare | Reproduce luciernagas del sur | PlaySongIntent (naked carrier) |
| famous | reproduce bohemian rhapsody | PlaySongIntent (famous title, free-text slot) |
| episode | reproduce adolescencia temporada uno episodio tres | PlayEpisodeIntent (library series, SeriesName consumer) |
| evergreen | reproduce musica rock | stable non-Fallback selection (genre control) |

## Static token pre-check (before any deploy)

Catalog snapshot from the authoritative plugin export (es-US: AudiobookTitle 234, SeriesName 160, JellyfinArtist 1137 values). Content-word overlap with the battery: AudiobookTitle ZERO (only the function word "la", 2 entries), SeriesName ZERO (only "la"/"del", 11 entries), JellyfinArtist one ("rock", artist "Billy Joel- It's Still Rock & Roll to Me", touching only the evergreen row). A pure content-word token collision therefore could not explain an audiobook-wired red on the censused row before the experiment even started; the live collision surface left for arm 4 to discriminate was function words and Amazon-side stemming/semantics.

## Consolidated result matrix (both passes identical in every cell; one line per cell)

| Wiring set | red_reproduce (acc+plain) | escucha | pon | toca | red_bare (naked) | famous | episode | evergreen |
|---|---|---|---|---|---|---|---|---|
| none (arm 1) | PlaySong GREEN | PlayArtistSongs steal | PlaySong | PlaySong | PlaySong GREEN | PlaySong | PlayEpisode | PlaySong (song=musica rock) |
| audio only (arm 2) | PlaySong GREEN | PlayArtistSongs steal | PlaySong | PlaySong | NO SELECTION (PlayBook in considered) | PlaySong | PlayEpisode | PlaySong |
| series only (arm 3) | PlaySong GREEN | PlayArtistSongs steal | PlayArtistSongs steal | PlaySong | PlaySong GREEN | PlaySong | PlayEpisode | PlaySong |
| synthetic audio only (arm 4) | PlaySong GREEN | PlayArtistSongs steal | PlaySong | PlaySong | PlaySong GREEN | PlaySong | PlayEpisode | PlaySong |
| artist only (arm 6) | PlaySong GREEN | PlaySong | PlaySong | PlaySong | NO SELECTION | PlaySong | PlayEpisode | PlaySong |
| audio+series (arm 7) | PlaySong GREEN | PlayArtistSongs steal | PlayArtistSongs steal | PlaySong | PlaySong GREEN | PlaySong | PlayEpisode | PlaySong |
| audio+artist (arm 8) | FALLBACK RED | PlaySong | PlaySong | PlaySong | NO SELECTION | PlaySong | PlayEpisode | PlaySong |
| series+artist (arm 9) | FALLBACK RED | PlaySong | PlaySong | PlaySong | PlaySong GREEN | PlaySong | PlayEpisode | PlaySong |
| all three (arm 5) | FALLBACK RED | PlaySong | PlaySong | PlaySong | NO SELECTION | PlaySong | PlayEpisode | PlaySong |

Reading keys: the censused row (red_reproduce with the carrier noun) is RED exactly in the three states containing the artist wiring AND at least one other wiring (arms 5, 8, 9) and GREEN in all six other states. The famous, episode, and evergreen rows never went red in any state (the evergreen row selects PlaySongIntent with song="musica rock" even unwired; its control value is stability, not genre routing). The escucha/pon columns show the free-musician-slot steal (PlayArtistSongsIntent with musician="luciernagas del sur") in exactly the states WITHOUT the artist wiring (the catalog-constrained musician slot no longer captures junk), and arm 3/7 show pon as a marginal steal to the same intent.

## Per-arm verdict contributions

Arm 1 (control): the census red row is green on the live skill the moment all wiring is removed, replicating the lab on the live skill itself; live-skill trainer state is ruled out as the cause.

Arm 2 (audiobook only): the censused row stays green; the NAKED row flips to no-selection with PlayBookIntent (the AudiobookTitle consumer) newly entering consideredIntents, so the wired-but-unmatched type injects its consuming intent as a competitor strong enough to break the naked form; the carrier noun armors the row.

Arm 3 (series only): green everywhere on the carrier row, and the arm-2 naked-row break does NOT reproduce, so that effect is audiobook-specific, not generic wiring presence; the episode row keeps selecting PlayEpisodeIntent with an unmatched wired SeriesName (a strongly carriered utterance survives its own intent's unmatched catalog slot). MODEL-WIDE single-type veto refuted here.

Arm 4 (synthetic values): identical to arm 2 except the catalog values cannot collide; the naked row is GREEN where arm 2 broke it. TOKEN COLLISION CONFIRMED as the value-content mechanism behind the arm-2 naked-row flip (the wiring block itself is innocent there); the censused carrier row is green under both value sets, so token collision cannot explain the census red.

Arm 5 (full wiring): the census red reproduces exactly (Fallback x2 on the reproduce row only) with a VERB INVERSION no other arm shows: reproduce Fallbacks while escucha/pon/toca of the same phrase route to PlaySongIntent. "Reproduce" is the model's most contested carrier (PlaySong, PlayEpisode, PlayByGenre, PlayBook and others all carry reproduce samples), so the reproduce shape is where combined unmatched-catalog competition collapses selection.

Arm 6 (artist only, added): GREEN on the censused row; the by-elimination attribution of arm 5's red to the artist wiring ALONE is refuted. Also the only single wiring whose own presence breaks the naked row with real values (arm 2's audiobook being the other), and it eliminates the free-musician-slot steal on every verb.

Arms 7-9 (pairs, added): complete the threshold map. audio+series GREEN (so two unrelated wirings still do not gate the carrier row); both artist-containing pairs RED (indistinguishable from the full triple). The conjunction structure is therefore: artist wiring (per-intent vulnerability on PlaySongIntent's own musician slot) PLUS at least one additional unmatched catalog wiring (model-wide cumulative pressure). Neither factor alone reproduces the red.

## What this implies for JF-960's pruning

The artist wiring is the necessary factor and one additional wiring is the sufficient cofactor, so the pruning arithmetic is now exact for this row: pruning audiobook OR series while KEEPING artist leaves an artist-containing pair (arm 8 / arm 9 shape) and the row stays RED; pruning the artist wiring fixes the row in every remaining state (audio+series green, any single green). The cheapest effective prune is the artist one, but it is also the most expensive functionally: the unwired musician slot reverts to free-text capture and the escucha/pon shapes then steal to PlayArtistSongsIntent with musician="luciernagas del sur" (a false artist query the plugin would fuzzy-search), visible in arms 1-4 and 7. The alternative mitigation is carrier-side rather than wiring-side: the failure is reproduce-verb-specific (pon/escucha/toca route green even at full wiring), so sample-side reinforcement or de-competition of the reproduce+carrier shape addresses the row without touching ER functionality. The decision stays with the maintainer; no fixture, template, or model file was edited in this experiment.

## What this implies for the naked-carrier-on-catalog-slot remedy

The naked form (`Reproduce luciernagas del sur`) is structurally unreliable in wired locales, and not monotonically: it breaks under real-valued audiobook-only and artist-only wiring, survives under series-only and synthetic-value wiring, breaks again under audio+artist and the full triple, and is green under audio+series and series+artist (n=2 per cell; the non-monotonicity is recorded, not explained). The media-noun carrier ("la canción") is green in every state except the three artist-containing multi-wiring states, so the qualified carrier remains the right prescription and anti-pattern #11's rule extends: in catalog-wired locales a naked carrier on a catalog-slot-adjacent intent risks selection collapse from value-content collisions (arm 2 vs arm 4) even when the slot in question is free-text.

## Restoration proof

After the last arm (arm 9), the pre-experiment live model (fetched byte-for-byte before the experiment) was redeployed and the build waited to SUCCEEDED. Final get-interaction-model readback: SeriesName wired to `amzn1.ask.interactionModel.catalog.1640b472-b7fb-42f7-844a-8b8a094a2499` v1298, AudiobookTitle to `...8b0a9dbf-8194-4026-b446-6a9c2982af4a` v608, JellyfinArtist to `...6590add1-6a8f-4460-9935-48345ae718f3` v1863, PlaySongIntent musician slot type JellyfinArtist. The fetched restored model is BYTE-IDENTICAL to the pre-experiment baseline fetch (sha256 prefix ed691262a10bcebc on both). Behavioral witness after restoration: the reproduce row reads AMAZON.FallbackIntent and the escucha row reads PlaySongIntent, exactly the pre-experiment live behavior. The plugin's rebuild endpoint was not needed (the redeploy IS the pre-experiment content); no other locale was touched at any point.

## Cleanup and residual artifacts

The hosting gist was deleted (raw URL now 404). The synthetic catalog `amzn1.ask.interactionModel.catalog.350015e1-d70e-498d-9897-c32d9bc0d3c3` could NOT be deleted: SMAPI refuses with `CATALOG_VERSION_IN_USE` ("Catalog version cannot be deleted because it is being used") because the arm-4/8 build history referenced version 1, and Amazon retains that link even after later builds stop referencing it. The artifact is inert: no live model references it, its values are pseudowords, and it is invisible to the household; deletion can be retried in a later session (delete-interaction-model-catalog / delete-interaction-model-catalog-version) once Amazon's usage link ages out. No repo file, backlog file, or plugin code was modified; this report is the only artifact, left uncommitted for coordinator review.

## Limitations

n=2 probes per cell with both passes identical everywhere, but two probes cannot bound rare trainer flips; the red/green pattern orders with the wiring factor, not with session time (green cells both precede and follow red cells), which argues against drift. The conjunction conclusion rests on 9 of the 8 Boolean wiring states plus one value-substituted variant; untested residual factors are the es-US trainer's response to sample-side changes and the interaction of the gate with other locales' different sample densities. The bare-row non-monotonicity (series+artist green, artist-only red) is recorded as an open observation. Arm 6 and arms 7-9 were additions beyond the designed five, motivated by the by-elimination ambiguity each introduced; both are documented in the design table.

## SMAPI call accounting

profile-nlu: 9 arms x 2 rebuilds x 9 rows = 162, plus 3 post-restore witnesses = 165. set-interaction-model: 18 arm rebuilds + 2 restoration deploys = 20. get-interaction-model: 1 baseline + 18 readbacks + 2 restoration proofs = 21. get-skill-status polls: roughly 4-6 per build x 20 builds. Catalog management: 1 list-skills, 1 create-catalog, 1 create-catalog-version, 1 update-status poll, 1 values read, 1 versions list, 4 delete attempts (refused). All profile-nlu calls at the 2s spacing; no other locale's model was deployed or probed.

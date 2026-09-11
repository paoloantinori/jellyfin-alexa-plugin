---
id: JF-541
title: >-
  it-IT musician-slot swap retry: re-pin the 23 pre-existing stale fixtures
  first, then re-apply the native swap and drive the ~12 attributable failures
  to zero (+ en stragglers)
status: In Progress
assignee: []
created_date: '2026-09-11 02:03'
updated_date: '2026-09-11 12:38'
labels:
  - nlu
  - interaction-model
  - it-IT
  - jf415-followup
  - fixtures
dependencies: []
references:
  - JF-415
  - JF-508
  - JF-523
  - JF-490
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from JF-415's it-IT extension rollback (2026-09-11 ~04:00, full evidence in JF-415's notes). NUMBERS: swapped it-IT model = 35 NLU-suite failures; pre-swap baseline (rolled-back deployed model, run the same night) = 23 failures, WITH THE SAME CLUSTER (Riproduci album thriller, Suona musica dei queen, suona matrix, disco thriller shapes, playlist casuale...). So ~12 failures are ATTRIBUTABLE to the native swap and ~23 are PRE-EXISTING stale fixtures (they fail identically without the swap; last verified green against an older catalog/model state, likely drifted by the weekly catalog syncs since).

STATE NOW: it-IT deployed model ROLLED BACK to pre-swap committed + catalog injection (which still re-types musician slots to JellyfinArtist via the injection path - so the deployed model DOES declare JellyfinArtist, provenance differs, and the runtime C# resolver matches both states). The COMMITTED repo model stays swapped (JF-415 merge): repo and deploy agree on the declared type, they differ on provenance (native block vs injection). The en-* swap is deployed and stays (en-GB fully green; the canonicalization fix verified).

SCOPE WHEN TAKEN: (1) re-pin the ~23 pre-existing stale it-IT fixtures FIRST (probe each, update expected winners or fix the model where a regression is unacceptable - 'Riproduci album thriller' routing to NO INTENT is the headline case to FIX, not re-pin); (2) then re-apply the native it-IT swap (redeploy the committed model + catalog rebind) and drive the ~12 attributable failures to zero via sample-level competition tuning (the JF-508/JF-490 toolkit: carrier words, competing-sample trimming); (3) the scattered en stragglers from the same night (e2e-class excluded per the documented en unreliability): en-US 'Repeat the song' (no intent), 'Play the song hotel california from the eagles' (song swallows the phrase, musician unfilled), 'Play imagine by john lennon next' (song='"Imagine" by John Lennon' with literal quotes), en-CA 'Play a random movie' (genre='movie' instead of media_type), en-IN 'play stranger things season two episode one' - probe, classify re-pin vs fix. NOTE for whoever runs the suite: the runner's own e2e tests inside the NLU script need the live endpoint and fail transients during model rebuilds; the 409-on-simulate class means another SMAPI consumer is active.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PHASE 1 COMPLETE (2026-09-11, merge 92539e42; task stays In Progress for phase 2). GROUND TRUTH (worker-established, changes the framing): the deployed and committed it-IT models are byte-identical in samples and slot types - the failures are entity-weighting drift under the LIVE CATALOGS (AlbumName v745, 880 values, contains NONE of the static seed albums; JellyfinArtist v739 lacks some in-library artists). Landed: 3 fixture re-pins (star wars x2 -> PlaySongIntent/song, the JF-470 steal gone under catalogs; DSOTM -> PlayAlbumIntent/album, the right intent), the dead seed-ER comment corrected, and the PlayVideoIntent bare-infinitive-carrier trim (Di riprodurre/Di suonare {title}) - DEPLOYED on the rolled-back base + catalog-rebound + probe-verified: 'Di suonare musica dei maneskin' now -> PlayArtistSongsIntent with the slot filled. Honest ledger of the trim: it ALSO flipped 'Di riprodurre l'ultimo episodio di stranger things' to a deterministic PlayByGenre steal (the verbatim episode sample exists and lost the statistical race) - phase-2 leftover. 9 model-bug leftovers documented for phase 2 (matrix pair, thriller family x4, musica family x2, stranger episodio): sample-level fixes are EXHAUSTED for them (worker's evidence-backed pushback); the real levers are (a) CATALOG ENRICHMENT - merge the static seed albums into the CatalogPayload upload so famous-but-not-in-library titles anchor (fixes the headline 'Riproduci album thriller' -> no-intent), and (b) the musica family needs an accept-and-repin or a phase-2 experiment decision (trimming PlaySong's 'Suona {song} dei {musician}' would break the working 'Suona comfortably numb dei pink floyd'). Suite evidence: the worktree run's NLU subset = the 9 documented + 2 throttle-flaky ('Musica energica', 'Riproduci film casuali' - passed in every earlier run, flaked under ~5h of continuous SMAPI use); its e2e flood is SMAPI throttling (literal 'SMAPI error' failures), not routing. PHASE 2 GATE: needs a fresh SMAPI quota window; re-apply the native swap on this re-pinned baseline + the catalog-enrichment C# change.

PHASE 2 (catalog-enrichment lever) COMPLETE (2026-09-11, merge 9d5b6f2d + deploy md5 11317ed8 + enriched sync live): CatalogSeedEnrichment extracts the static seed values from the embedded interaction models at runtime (albums from the it-IT model per JF-332; artists as the 12-locale union; Series excluded with a one-time disabled warning) and merges them into the AlbumName/JellyfinArtist uploads (library-wins dedup, same truncate+phonetic pipeline, stable SHA ids). LIVE-VERIFIED: the sync logs show Artist +2 / Album +9 appended, and the HEADLINE CASE IS FIXED - 'Riproduci album thriller' and 'Metti il disco thriller' both now route PlayAlbumIntent album='thriller' (were: no-intent / PlaySong steal). FINAL LEDGER vs the 13-failure baseline: 8 RESOLVED (thriller family x4 via enrichment; star wars x2 + DSOTM via phase-1 re-pins; maneskin via the PlayVideo trim; 'Riproduci i queen' + 'Suona gli soul coughing' stabilized by the enriched catalog version - their earlier suite failures were mid-rebuild transients, both pass on targeted re-run), 5 DOCUMENTED leftovers (matrix pair: catalog-unfixable, matrix is not an artist; musica family x2: the open accept-vs-experiment decision; stranger-episodio: the trim's deterministic collateral). The final suite run (20 failed) decomposes as the 5 documented + 2 proven-transients + 13 e2e-class endpoint failures ('An unexpected error occurred' simulate-skill instability, chronic in these runs, not routing). REMAINING (phase 3): re-measure the native-swap delta on this fixed baseline in a fresh SMAPI window (the pre-phase-1 measurement of ~12 swap-attributable failures predates the enrichment + re-pins); the musica-family decision; the en stragglers. NOTE: Paolo instructed (2026-09-11) that minix config modifications delegate to the herdr window minix_ansible_config - the LastCatalogSync XML aging in this cycle predates the instruction; future config edits go through that window.
<!-- SECTION:NOTES:END -->

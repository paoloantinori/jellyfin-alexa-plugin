---
id: JF-298
title: >-
  "Play song X" utterances route to FallbackIntent for non-catalog titles (it-IT
  catalog-slot gap)
status: Done
assignee:
  - claude
created_date: '2026-06-18 08:37'
updated_date: '2026-06-18 14:56'
labels:
  - bug
  - interaction-model
  - nlu
  - alexa
  - it-IT
dependencies: []
references:
  - /home/pantinor/.cc-mirror/zai/config/plans/stateful-skipping-zebra.md
  - >-
    Alexa/InteractionModel/templates/it-IT.yaml (FindSongIntent explicit_intents
    ~L396-411; PlaySongIntent templates; vocabulary L12-15)
  - Alexa/InteractionModel/model_it-IT.json (generated)
  - >-
    Alexa/Handler/Intent/FindSongIntentHandler.cs:472-485 (auto-play on single
    match)
  - tests/integration/fixtures/it-IT.yaml (FindSong fixtures ~L626)
  - >-
    scripts/generate_interaction_model.py,
    scripts/validate_interaction_models.py, scripts/run_nlu_tests.sh
  - >-
    commit c3d6a40 (fix/findsong-play-verbs-jf298): it-IT.yaml +40 samples,
    model_it-IT.json regenerated, fixtures/it-IT.yaml +2 fixtures
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_it-IT.json
  - tests/integration/fixtures/it-IT.yaml
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Symptom (user report)

Saying (it-IT) *"chiedi a mia collezione di mettere la canzone radioestensioni"* always replies *"Non ho capito"* ("I didn't understand"). Reproduced 3× in live logs.

## Root cause (diagnosed from Jellyfin logs, debug logging on)

- The skill **was** invoked (invocation name `mia collezione` works). All three attempts routed to `AMAZON.FallbackIntent` → response `"Non ho capito, per favore riprova."` with **no slots**.
- `PlaySongIntent` *has* the matching sample `Di mettere la canzone {song}`, but its `song` slot is **`AMAZON.MusicRecording`** — a *catalog* slot type. "radioestensioni" isn't in Amazon's catalog, so the slot can't be filled → Alexa's NLU rejects the utterance for `PlaySongIntent` and sends it to `FallbackIntent` **before any handler runs**.
- `FindSongIntent` uses **`AMAZON.SearchQuery`** (free-text, single slot `titleKeywords`) and `FindSongIntentHandler` **auto-plays on a single match** (`FindSongIntentHandler.cs:472-485`). It would capture "radioestensioni" and play correctly — but its it-IT samples only cover *search* verbs (`cerca`/`trova`), not *play* verbs (`metti`/`riproduci`/`di mettere`). So the user's phrasing falls into the gap.

This is the classic catalog-slot trap: built-in `AMAZON.MusicRecording`/`Musician` types are useless for a Jellyfin library of non-catalog tracks.

## Fix (scope: it-IT only for this hotfix)

Add **free-text play-verb samples** to `FindSongIntent` (it-IT) so the natural "play/metti/riprodurre la canzone X" phrasing routes there. Single-slot SearchQuery ⇒ no coexistence violation; `FindSongIntent` already in `dialog.intents` (anti-pattern #9 ✓). Benign NLU overlap with `PlaySongIntent` (both play correctly; only unknown titles need FindSong). The other 16 locales roll out in a follow-up.

## Log evidence (minix jellyfin container, 2026-06-18 ~08:19-08:21)

- `corr=8aea1659 / 1bfadbd2 / 68fe350d` — `IntentRequest intent=AMAZON.FallbackIntent locale=it-IT`, response `"Non ho capito, per favore riprova."`, no slots.
- `FindSong: entered, intent=AMAZON.FallbackIntent` (FindSongIntentHandler intercepts Fallback but has no session/slot → standard fallback).

Full test-first methodology (baseline → RED → change → no-regression → on-device) is in the Implementation Plan field and in the plan file.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 BASELINE captured BEFORE any model edit: full NLU suite (scripts/run_nlu_tests.sh) run against the currently-deployed SMAPI model; per-locale pass/fail recorded as the regression reference; pre-existing failures noted (not fixed here)
- [x] #2 RED: two new it-IT fixtures added to tests/integration/fixtures/it-IT.yaml ("metti la canzone radioestensioni" and "di mettere la canzone radioestensioni" → FindSongIntent / titleKeywords) and confirmed FAILING on the current deployed model (proves they are meaningful)
- [x] #3 Model change (it-IT only): 40 play-verb free-text samples added to FindSongIntent in templates/it-IT.yaml explicit_intents — imperative×song_noun×{titleKeywords} (20) + infinitive×song_noun×{titleKeywords} (20), including the user's exact phrasing 'Di mettere la canzone {titleKeywords}'; model_it-IT.json regenerated via generate_interaction_model.py; git diff shows +40 sample lines and nothing else
- [x] #4 No bare '{verb} {titleKeywords}' samples added (would be greedy and siphon PlayPlaylist/PlayByGenre); every new sample keeps a song_noun carrier
- [x] #5 validate_interaction_models.py passes (single-slot SearchQuery; no new cross-locale drift; no duplicate samples)
- [x] #6 GREEN + no-regression: after deploying it-IT via SMAPI, the FULL NLU suite re-runs; new fixtures pass (FindSongIntent); the only allowed diffs vs baseline are expected known-title PlaySong→FindSong flips (both play correctly); any other regression investigated/reverted
- [ ] #7 On-device (it-IT) confirmed: saying 'chiedi a mia collezione di mettere la canzone radioestensioni' plays the song; podman logs jellyfin shows a FindSongIntent arriving (not AMAZON.FallbackIntent) and playback starting
- [ ] #8 Rollout of the same pattern to the other 16 locales tracked as a separate follow-up (not done in this task)
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-06-18 12:20
---
Verified + committed (it-IT hotfix). Baseline it-IT NLU: 110 pass / 2 pre-existing film-related fail (regression ref). RED: new fixtures ('metti/di mettere la canzone aspettando il sole' -> FindSongIntent) failed on pre-deploy model (routed PlaySong). Implemented: +40 play-verb free-text samples to FindSongIntent (validate_interaction_models.py PASS). Deployed it-IT via SMAPI (build SUCCEEDED; the initial ECONNRESET was misleading — Amazon accepted it). GREEN + no-regression: full it-IT suite 114 pass / 0 fail, NO PlaySong->FindSong flips on known catalog titles, zero new regressions. Routing confirmed via profile-nlu; handler confirmed via plugin Simulator (live Jellyfin). simulate-skill unreliable here (5 installed skills compete; utterance routed to <IntentForDifferentSkill>) — not a fix failure, expected per CLAUDE.md. NOTE: fixtures use the REAL Jellyfin track 'Aspettando il sole' (Neffa) per user — 'radioestensioni' was dropped because the track doesn't exist in the library. UX note: FindSong is multi-turn (elicits artist 'Chi e l'artista?' before playing) — the Fallback bug is fixed; physical-Echo confirm + 16-locale rollout remain.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
REVERTED as not-a-bug. On-device test (logs 2026-06-18 16:41:25) proved "metti la canzone aspettando il sole" routed to PlaySongIntent and played via the EXISTING PlaySong path (AMAZON.MusicRecording slot captured the spoken title) — the FindSong fix was never used. The original diagnosis ("catalog slot can't capture unknown titles") was WRONG: the slot captures free text. The original "radioestensioni" failure was a coined/made-up word (ASR limitation, unfixable via interaction model) and the track doesn't exist in the library anyway. Commit c3d6a40 reverted in a0fc04f; original it-IT model redeployed to SMAPI (build SUCCEEDED, FindSong back to 15 samples). Conclusion: no real bug; PlaySong by full title already works for real tracks. (The genuinely real log issue is JF-299, the InvalidResponse/shouldEndSession error.)
<!-- SECTION:FINAL_SUMMARY:END -->

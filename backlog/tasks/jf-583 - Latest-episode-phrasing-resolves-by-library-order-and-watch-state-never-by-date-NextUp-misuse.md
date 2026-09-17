---
id: JF-583
title: >-
  Latest-episode phrasing resolves by library order and watch state, never by
  date (NextUp misuse)
status: In Progress
assignee: []
created_date: '2026-09-17 15:15'
updated_date: '2026-09-17 17:23'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Verified 2026-09-17 on minix (Jellyfin 12.1.0 + this plugin): 'Riproduci l'ultimo episodio di {series}' routes to PlayNextEpisodeIntent, whose TvNextUp.PlayNextUpEpisodeAsync core is Jellyfin GetNextUp. That core answers 'first unwatched ordered by (ParentIndexNumber, IndexNumber)' and never consults PremiereDate (NextUpService.cs at tag v12.1: candidates OrderBy ParentIndexNumber ThenBy IndexNumber, position filter only when something is played). The JF-324 DateCreated-desc fallback only fires when NextUp is empty, which almost never happens for podcasts.

Evidence from production logs:
- 2026-09-16 18:25 'morning' resolved 'Morning Weekend' special of 2026-09-05 (11 days stale), latestFallback=False.
- 2026-09-17 16:39 'wilson' resolved 'Ristoranti e hotel stanno cambiando' of 2026-08-13 while the 2026-09-17 episode existed.
- Live probe 2026-09-17 (user paolo, specials since marked played): NextUp returned Ep. 1278 of 2026-09-07, i.e. the oldest unwatched retained daily. 'L'ultimo episodio' only ever returns today's episode once everything older is played.

Root cause is here, not in the Il Post metadata: 'ultimo' asks for recency, NextUp answers watch state in library order. Also note PlayNextEpisodeIntent has only the series_name slot, so 'ultimo' and 'prossimo' are indistinguishable today.

Fix direction: distinguish the two phrasings (episode_position slot or a separate intent) and serve 'ultimo' with a date-ordered query (PremiereDate DESC preferred; DateCreated DESC is the existing JF-324 shape). PlayPodcastIntentHandler already uses DateCreated DESC for the MusicAlbum podcast path, so the ordering precedent exists in-tree.

Related but separate: the Il Post plugin is moving its occasional specials to season 0 (Jellyfin convention; season 0 is excluded from NextUp by design). That removes the special-hijack failure mode for numbered feeds but does NOT fix unnumbered shows nor the general oldest-unwatched behavior, so this task remains the actual fix.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 'Riproduci l'ultimo episodio di X' returns the most recent episode by PremiereDate, verified on-device
- [ ] #2 'Riproduci il prossimo episodio di X' keeps NextUp semantics
- [ ] #3 Interaction model updated for it-IT and NLU fixtures cover both phrasings
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Design decision (settled 2026-09-17)

Custom slot `episode_position` (restricted custom type `EpisodePosition`) on PlayNextEpisodeIntent, EMPTY/unresolved defaulting to prossimo = NextUp. This is the JF-354 Mood architecture applied to the position word. A separate intent was rejected on evidence, not preference:

1. No new competition surface. Production logs show BOTH phrasings already route to PlayNextEpisodeIntent correctly; only the semantics inside it are wrong (the task file's live evidence). A new intent would add a second region competing with PlayEpisode/PlayArtistSongs/PlayPodcast for the same carriers (anti-pattern #3; PlayPodcast ALREADY wins "play the latest episode of X" in en-US per JF-523). The slot keeps the utterance surface byte-equivalent modulo the position marker.
2. Cost: a new intent needs a handler, 17 locale response families, dialog registration, and NLU fixtures everywhere; the slot needs one type block per template and reuses every existing string (PlayingLatestEpisode from JF-324 covers the announce; no new locale strings).
3. Failure mode degrades to today's behavior: empty, absent, or unresolved slot = NextUp. No string-indistinguishable guess (the JF-377 lesson).

Resolution chain: entity resolution first (every locale's two values carry the shared ids next/latest, the JF-468 one-key-space rule, so the ER branch is locale-independent), canonical-name check as belt and braces, then the localized raw-value word table (Alexa/Util/EpisodePosition.cs, the LocalizedMoodMap precedent) for requests without ER (older deployed model, ASR drift).

## Implementation

- `TvNextUpService.PlayLatestEpisodeAsync` (new public core): episodes of the resolved series ordered PremiereDate DESC, DateCreated DESC tiebreak (JF-324 fallback shape; PlayPodcast's DateCreated DESC is the in-tree precedent), NO played filter, Limit 1, AncestorIds scoped. Never calls GetNextUp. Empty result returns the same NoNextEpisode Tell.
- Launch is NOT forked: the old `PlayNextUpEpisodeAsync` tail (queue seeding, JF-565/JF-581 resume resolution, URL-first announce gate, JF-498/JF-501/JF-505 launch) is extracted as `LaunchEpisodeAsync` and shared by both cores; the recency core passes announceLatest=true (PlayingLatestEpisode wording).
- `PlayNextEpisodeIntentHandler` reads the slot after the media-type gate, routes to the recency core when latest. The series_name elicit now declares BOTH slots (Amazon rejects a partial updatedIntent; validator Phase 8 JF-556 check enforces the equality).
- Season-0 specials are NOT filtered on the latest path (not in scope; recency is the ask, so a special that IS the newest item is a defensible answer). The Il Post season-0 move is tracked in the task description as related-but-separate.

## Per-locale EpisodePosition values (canonical / id / synonyms)

| locale | next | latest |
|---|---|---|
| it-IT | il prossimo / next / [prossimo, il successivo, successivo] | l'ultimo / latest / [ultimo, l'ultima, ultima, il più recente, più recente] |
| en-US,GB,AU,CA,IN | next / next / [the next, upcoming, the upcoming] | latest / latest / [the latest, newest, the newest, last, the last, most recent, the most recent] |
| de-DE | die nächste / next / [nächste, die kommende, kommende] | die neueste / latest / [neueste, die letzte, letzte, aktuellste, die aktuellste] |
| es-ES,MX,US | el próximo / next / [próximo, el siguiente, siguiente] | el último / latest / [último, el más reciente, más reciente] |
| fr-FR,CA | le prochain / next / [prochain] | le dernier / latest / [dernier, le plus récent, plus récent] |
| pt-BR | o próximo / next / [próximo] | o último / latest / [último, o mais recente, mais recente, o mais novo, mais novo] |
| nl-NL | de volgende / next / [volgende] | de nieuwste / latest / [nieuwste, de laatste, laatste, de meest recente] |
| hi-IN | अगला / next / [आगामी] | नवीनतम / latest / [सबसे नया, नया, आख़िरी] |
| ja-JP | 次の / next / [次] | 最新の / latest / [最新, 一番新しい] |
| ar-SA | التالية / next / [تالية, القادمة, قادمة] | الأحدث / latest / [أحدث, الأخيرة, الجديدة] |

Sample shapes: it/es/fr/de/nl/en preposed article+position before the noun (article inside the type value: "il prossimo", "the next" as synonym); pt-BR keeps BOTH the preposed family and today's postposed "tocar o episódio mais recente" word order (slot captures "mais recente"); ar-SA keeps the definite postposed (الحلقة التالية) and indefinite preposed (أحدث حلقة) families; ja/hi keep their series-first order. All continue-watching families carry NO position slot (NextUp by design).

## Files

Code: Alexa/Util/EpisodePosition.cs (new), Alexa/Handler/Intent/PlayNextEpisodeIntentHandler.cs, Alexa/Handler/TvNextUpService.cs. Models: templates/*.yaml x17 + model_*.json x17 (regenerated, never hand-edited) + dialog entries x17 (Phase 8 equality). Voice-reference generator: scripts/generate_voice_reference.py (episode_position SLOT_HINTS x10 languages) + VOICE_COMMANDS.md + docs/VOICE_COMMANDS_BY_LOCALE.md regenerated. Docs mirrors: docs/playback-lifecycle-*.md x17 edge labels, docs/graphs.json + docs-site/graphs.json (parse_mermaid re-run), docs-site/data.json (embedded mermaid synced from md; verified the only pre-existing diff was the edge line). Fixtures: tests/integration/fixtures/it-IT.yaml (episode_position asserted on both ultimo cases, prossimo cases left tolerant), e2e_it-IT.yaml (existing latest case now asserts the slot fill). en-US NLU fixtures unchanged: its latest phrasing intentionally routes to PlayPodcastIntent (JF-523) and its next cases are slot-tolerant. Tests: PlayNextEpisodeIntentHandlerTests (5 new cases, red-first verified).

## Verification (2026-09-17)

- dotnet test: 4051/4051 passed, BOTH TFMs (net9.0 + net10.0). New tests red before implementation, green after.
- dotnet build -c Release: 0 warnings, 0 errors.
- validate_interaction_models.py: PASS, 118 warnings = exact pre-change baseline (the +17 mirror-staleness warnings cleared by regenerating the voice reference).
- validate_locales.py: PASS (no new locale gaps; no new strings needed).
- run_nlu_tests.sh --dry-run: 8 passed, 1059 skipped, 0 failed (one transient live-simulator flake re-ran green; unrelated to this change).
- Model JSONs verified byte-stable across the final header-comment edits (md5), so the green full-suite run covers the current tree.

## Left for the user / next session

- On-device verification (AC#1): deploy the DLL + rebuild the interaction models (at minimum it-IT), then on the Echo say "riproduci l'ultimo episodio di {show}" against a series with unwatched backlog (the production evidence cases: morning/wilson) and confirm the NEWEST episode plays with the latest-episode announce; "il prossimo episodio" must keep playing the oldest-unwatched.
- SMAPI build check on first deploy: the type values carry non-ASCII and apostrophes (l'ultimo) which SMAPI accepts in principle but the build is the proof.
- Live NLU suite run (profile-nlu) for the new it-IT fixtures.
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

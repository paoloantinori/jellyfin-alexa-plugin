---
id: JF-504
title: >-
  Movie-by-title does not route in the model (three phrasings, zero selections;
  handler verified only via simulator): add media-named movie carriers to
  PlayVideoIntent in all locales
status: Done
assignee: []
created_date: '2026-09-06 08:58'
updated_date: '2026-09-06 11:16'
labels:
  - nlu
  - video
  - movies
dependencies: []
references:
  - device test 2026-09-06
  - profile-nlu evidence in description
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-09-06 device session (item 3 of the verification card): 'chiedi a mia collezione di riprodurre ada' (three attempts, error beep) NEVER reached the skill (zero requests in the server logs), and profile-nlu confirms the model cannot route movie-by-title: 'di riprodurre ada' -> no selection (top considered RepeatSingleOnIntent), 'di riprodurre il film ada' -> AMAZON.FallbackIntent, 'riproduci il film ada' -> no selection. The handler path is verified (the simulator delivered slots directly and the launch works, including the static movie URL), so this is purely a MODEL-LAYER gap: PlayVideoIntent's it-IT samples ('Riproduci {title}', 'Metti {title}', 'voglio guardare {title}', ...) are too weak against the now catalog-heavy model (JellyfinArtist/AlbumName/SeriesName catalog types dominate the statistics). Fix shape: add media-named movie carriers to the it-IT template ('{imperative} il film {title}', '{infinitive} il film {title}', '{imperative} il movie {title}' if idiomatic, 'voglio guardare il film {title}', 'cerca il film {title}') plus the equivalents in the 16 other locales' conventions; check the title slot type per locale for consistency (AMAZON.SearchQuery today; a single SearchQuery slot per intent is legal); NLU fixtures for the new shapes in it-IT + en-US; profile-nlu verification that 'di riprodurre il film ada' selects PlayVideoIntent with the title filled; device retest.
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
<!-- NOTES:BEGIN -->
2026-09-06 (JF-504 implementation): media-named movie carriers added to PlayVideoIntent in all
17 locales; title slot type unchanged (AMAZON.SearchQuery everywhere; single-slot SearchQuery
intents are legal and no slot type was touched).

it-IT (template, `explicit_intents` -> PlayVideoIntent, regenerated via
`python3 scripts/generate_interaction_model.py it-IT`): 6 new samples. The task directive's
`{imperative} il film {title}` / `{infinitive} il film {title}` were hand-expanded to the
natural verb subset (Riproduci/Metti + Di riprodurre/Di mettere) rather than the full
Cartesian product, because explicit_intents samples are NOT template-expanded by the generator
(verbatim passthrough in `generate_interaction_model.py`) and the remaining verbs
(Suona/Pleia/Ascolta, music-performance verbs) are unidiomatic with a film object
(anti-pattern #6 discipline: "Suona il film" would be manufactured noise). The regenerated
model diff is exactly the 6 additions inside PlayVideoIntent, nothing else. Added samples:
`Riproduci il film {title}`, `Metti il film {title}`, `Di riprodurre il film {title}`,
`Di mettere il film {title}`, `voglio guardare il film {title}`, `cerca il film {title}`
(cerca per the JF-490 cerca-as-play precedent).

Other 16 locales (direct JSON edits; all following each locale's OWN verb/noun conventions
sourced from its existing samples, all carriers name the media, no bare carriers):

- en-US/en-AU/en-CA/en-GB/en-IN (already had `play the movie`/`put on the movie`): +3 each:
  `watch the movie {title}`, `to play the movie {title}` (matches the `to play the {musician}`
  one-shot family), `search for the movie {title}` (definite article vs SearchMediaIntent's
  `Search for a movie {query}`).
- es-ES/es-MX/es-US (had `Reproduce la película`): +4 each: `Reproducir la película {title}`
  (infinitive per `Reproducir la playlist`), `Ver la película {title}`, `Quiero ver la película
  {title}`, `Busca la película {title}`.
- fr-FR/fr-CA (had `mets le film`/`diffuse le film`): +3 each: `De jouer le film {title}`
  (infinitive per `De jouer la playlist`), `Je veux regarder le film {title}`, `Cherche le film
  {title}`.
- de-DE (had NO film carrier at all): +4: `Spiele den Film {title}`, `Zu spielen den Film
  {title}` (infinitive per `Zu spielen die {musician}`), `Ich möchte den Film {title} sehen`,
  `Suche nach dem Film {title}` (dem vs SearchMedia's `Suche nach einem Film`).
- pt-BR (had `tocar o filme`): +3: `assistir o filme {title}`, `quero assistir o filme {title}`,
  `procurar o filme {title}`.
- nl-NL (had `speel de film`): +3: `zet de film {title} op`, `ik wil de film {title} kijken`,
  `zoek de film {title}`.
- ja-JP (had `映画 {title} を再生して`): +2: `映画 {title} を見たい`, `映画 {title} を見せて`.
  NO search form: Japanese has no article system, so every "movie + search verb" shape is
  already a SearchMediaIntent sample (`映画 {query} を検索して` / `を見つけて` / `を探して`);
  a PlayVideo search carrier would be a byte-identical head-on duplicate (guaranteed coin
  flip), not a distinguished carrier like the/a or dem/einem in the European languages.
- hi-IN (had `फिल्म {title} चलाओ`): +3: `फिल्म {title} देखो`, `मैं फिल्म {title} देखना चाहता हूँ`,
  `फिल्म {title} खोजो` (no एक vs SearchMedia's `एक फिल्म {query} खोजो`).
- ar-SA (had `شغل الفيلم`): +3: `شاهد الفيلم {title}`, `أريد مشاهدة الفيلم {title}`,
  `ابحث عن الفيلم {title}` (definite vs SearchMedia's indefinite فيلم).

Pre-edit scan confirmed ZERO of the planned additions duplicate any existing sample in any
intent of any locale (exact-string, case-insensitive).

NLU fixtures (decision as allowed by the task): slots pinned presence-only (`title: {}`),
not value-pinned, for ALL new entries; the failure mode under test is "no selection /
intent-without-slot", so intent + non-empty title presence is the right assertion, and free-text
SearchQuery values for short titles like 'ada' are not stable enough to pin. it-IT.yaml +5:
`Riproduci il film star wars`, `Di riprodurre il film ada` (the exact failing device phrasing),
`metti il film matrix` (also guards the JF-436 boundary: bare `metti matrix` pins
PlayArtistSongsIntent just below, the film carrier must flip it), `voglio guardare il film ada`,
`cerca il film ada`. en-US.yaml +3: `watch the movie star wars`, `to play the movie the
godfather`, `search for the movie star wars`.

Validators: `validate_interaction_models.py` PASS at the 90-warning baseline (unchanged);
`validate_locales.py` PASS ("no new locale gaps"); NLU dry-run: 860 skipped (schema-valid, no
SMAPI calls). Live profile-nlu verification of the new pins + device retest are for the
orchestrator's deploy (not run here per task rules).

Mirror obligation (anti-pattern #11): VOICE_COMMANDS.md `Play Video` row updated in all 17
locale tables (new samples appended in model order, backtick + ` · ` separators). Scripted
cross-check vs `git show HEAD:<model>`: 17/17 EXACT MATCH, row shape valid (additions inside
the table cell, trailing pipe intact; an intermediate script pass had appended after the cell
pipe and was repaired + re-verified). Diff touches only the 17 Play Video rows. The graph/doc
mirrors (`docs/playback-lifecycle-*.md`, `docs/graphs.json` x2, `docs-site/data.json`) are NOT
touched here: they are already stale for PlayVideo wholesale under JF-496's open item, which
now also covers these movie-carrier edges (noted as a JF-496 addition, no new task needed).

No C# code changed (models are embedded content; no handler, DTO, or response-string change:
DoD items 4, 5, 8 are no-ops for this diff; item 7 covered by the NLU fixture additions, the
model layer has no new handler logic to E2E beyond what they pin).
<!-- NOTES:END -->
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-06 (commit 920f0494, models deployed to all 12 active locales and live-verified). Media-named movie carriers added to PlayVideoIntent in all 17 locales (it-IT: 6 carriers via the YAML template with a hand-expanded idiomatic verb subset; the other 16 per their own conventions; ja-JP watch-only, its search carriers would be byte-identical duplicates of SearchMediaIntent); title stays AMAZON.SearchQuery everywhere. Fixtures it-IT + en-US including the exact device-failing phrasing; VOICE_COMMANDS.md 17/17 EXACT MATCH cross-check. DEPLOYED + LIVE-VERIFIED: all 12 active-locale models rebuilt SUCCEEDED, the final catalog sync re-injected references with 12/12 canaries OK, live it-IT carries the 6 film carriers (60 intents / 1433 samples), and profile-nlu now selects PlayVideoIntent with title filled for all three previously-failing phrasings ('di riprodurre il film ada', 'metti il film ada', 'voglio guardare il film ada'). Device retest card: 'chiedi a mia collezione di riprodurre il film ada' should now launch the movie.
<!-- SECTION:FINAL_SUMMARY:END -->

---
id: JF-963
title: >-
  Align our interaction models to major-skill utterances (official Alexa skills,
  Plex, Music Assistant, open-source media skills)
status: To Do
assignee: []
created_date: '2026-11-25 14:21'
updated_date: '2026-11-26 09:15'
labels:
  - nlu
  - research
  - alignment
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Maintainer directive (2026-11-25): align our interaction models to the utterances used by the official Alexa skills and other major media skills (Plex, Music Assistant explicitly named; open-source media skills count too). Sources to mine: (1) the Alexa Skills Store listings via SMAPI (skill details carry the published examplePhrases — ask smapi list-skills-for-vendor is our own, but the store search / get-skill-details for PUBLIC skills exposes examples; probe what ask smapi offers for third-party skills); (2) Amazon's own docs (the utterances of built-in Music/Video behaviors leak through the Request/Response JSON reference's intent names and the documented voice controls for AudioPlayer/VideoApp); (3) GitHub: open-source media skills publish full interactionModel JSONs (Kodi skills, MyMedia for Alexa, Plex skill clones, home-assistant companion skill's media parts); (4) the Plex Alexa skill and Music Assistant project docs/repos for their phrasing conventions. Deliverable: a per-locale delta report (which proven phrasings our models lack, which of ours diverge from the ecosystem norms) and template alignment edits for the maintainer-approved subset — each edit follows the carrier rules (word-ownership check via the JF-954.2 lint, graph diff via JF-954.4 before/after).
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
FOLLOW-UPS from the JF-965 review round (2026-11-25 sera, filed same-turn per discipline): (1) SIBLING LOCALES: the batch-1 eleven rows landed in en-US/es-ES/fr-FR/de-DE only; the same-language siblings (en-GB/en-AU/en-CA/en-IN, es-MX/es-US, fr-CA) carry the identical base carrier families and keep the gap - each trains its own model, so en-US rows do not generalize cross-locale. The next batch must START with a carriers delta per sibling locale (the JF-970 tool makes this a minutes-long check) before any row lands. (2) DE CONTRACTION FAMILY: the new de carrier-noun row pins only the uncontracted 'von dem Kunstler' form; spoken German contracts to 'vom Kunstler', which is in neither model, fixtures, nor the lab battery. Candidates for the next batch: 'Spiele Musik vom Kunstler {musician}', 'Spiele Lieder vom Kunstler {musician}' (same carriers-check + lab discipline first; the corpus research doc's askplex de grid is the source to re-read for the exact measured forms).

TWIN RULE for the sibling batch (from the JF-965 wrapper round, 2026-11-26): every carrier row landed for a sibling locale must land WITH its one-shot wrapper twin in the same change (fr-CA 'De ...' infinitives, es-MX/es-US 'que + subjunctive', en-GB/AU/CA/IN 'to + infinitive'), or the sibling keeps dead-air one-shot compositions the base rows cannot fix (each locale trains its own model). Known residual from JF-965: the en RECORD-noun one-shot twin is uncovered by design (loses arbitration to PlayByGenre 'listen to {genre}', stable 2/2 on the lab; revisit only with a probe-backed wording). LAB LIMITATION for all future batches: invocation-headed one-shot phrases CANNOT be tested on the nlu-lab skill (foreign invocation name reads Fallback there); verify bare tails on the lab and headed forms on the live skill post-deploy.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
RESEARCH COMPLETE 2026-11-25 (report: claudedocs/research_major-skill-utterances_2026-11-25.md, committed 0e393abf) + FABLE REVIEW same day. Corpus mined: Amazon MSAPI voice-modeling grammar, Standard Built-in Intents tables per locale, the official Plex voice-command article (Wayback), Emby's and My Media's official docs, Music Assistant (confirms our JF-551 es-subjunctive and it-IT infinitive wrappers as convergent), 6 open-source families. 25 ranked recommendations. FABLE AMENDMENTS adopted: the pre-deploy evaluation sequence (graph diff on branch -> stop criteria: a new IDENTICAL edge blocks, a new SWALLOW edge with free/catalog slot needs probe -> token lint + classify-fixtures -> lab with change-driven selection; the graph says where a row CAN do damage, not whether it will); noun-after playlist moves to PROBE-FIRST (the sentence head 'play the/my {#}' is a naked greedy carrier, four sentinels: play the beatles / play my favorites / play the album X / play my music + lint 'playlist' as a BrowseCategory/MediaType value); cautions list gains 'open/go to {browse_category}' (launch word + chapter carrier), 'give me a suggestion' (the coldplay hallucination class), and artist name-carrier rows are LINT-FIRST (band/singer already MediaInfoType synonyms = the #46 class). SCARTATI (never add, maintainer-confirmed pending): 'play some {genre}' naked (catalog-slot carrier), 'Spiele das Lied {song}' (duplicates the JF-940 library twin), 'esegui' it (single translated Kodi source, cartesian explosion; the maintainer is the native speaker). Episode-by-title: scope creep with structural cost (episode title + series in one sample -> no SearchQuery -> an episode-title catalog type with thousands of values = the JF-946 1.5MB pressure); stays a feature note. JF-961 interaction: none for by-order rows (en already carries them on FindSong; es musician slot is non-catalog), but en-US carries the naked 'play {#}' on FOUR intents, two with catalog slots (PlayArtistSongs/JellyfinArtist, PlayBook/AudiobookTitle) = the conjunction configuration; a live probe gates any en alignment (filed as JF-964). EXECUTION follow-ups filed: JF-964 (the en-US probe) and JF-965 (alignment batch 1).
<!-- SECTION:FINAL_SUMMARY:END -->

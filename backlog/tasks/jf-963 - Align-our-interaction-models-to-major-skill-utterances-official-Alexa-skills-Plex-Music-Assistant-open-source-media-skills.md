---
id: JF-963
title: >-
  Align our interaction models to major-skill utterances (official Alexa skills,
  Plex, Music Assistant, open-source media skills)
status: Done
assignee: []
created_date: '2026-11-25 14:21'
updated_date: '2026-11-27 21:18'
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
BATCH 2 (SIBLING + CONTRACTION) COMPLETE and pushed (merge + acf0372d; the batch-2 worker commit 586449bc through the merge): the JF-965 batch-1 alignment rows now cover every same-language sibling plus the German contraction family. LANDED: en-GB/AU/CA/IN PlayAlbumIntent +3 each (start the album/record + the album twin; the record twin excluded with the measured-steal comment); es-MX/es-US PlayArtistSongsIntent +6 each (3 capital-R base + 3 que-subjunctive twins); fr-CA +4 (2 Joue base + 2 De infinitive twins); de-DE +2 vom-contraction rows (Spiele Musik/Lieder vom Künstler) AND +2 zu-wrapper twins (Zu spielen Musik/Lieder vom Künstler) added AFTER the lab arm proved they survive arbitration (run 20261127-221549: both route PlayArtistSongsIntent with musician filled, zero competition). LAB VERIFICATION (the JF-965 discipline per the task notes): all 7 siblings + de template + de zu-arm green, A/A zero everywhere (runs align-* 20261127-21xxxx/22xxxx); every new row and every twin routes with slots filled. The 6 new es-sibling MediaInfoType collision members baselined per the JF-965 precedent (the class JF-933 live-adjudicated benign). The fr-CA PlayByGenre SWALLOW +2 confirmed as the known guarded edge (identical to fr-FR's, sentinels carried). Fixtures: bare-tail rows per locale (twins as tails); es-US rows carry the JF-406 divergence escape-hatch note; mirrors regenerated (voice reference, graph no-op byte-identical). TASK COMPLETE: the research corpus (25 recommendations), Fable amendments, batch-1 (4 lead locales), and batch-2 (7 siblings + contractions + zu twins) are all delivered; the probe-first and scartati lists stand as documented decisions; the residual one-shot live confirmation rides the next deploy (standing vai-gated rule). gate-exempt: model-data-only change (no product code); the full static battery + NLU dry-run + lab rounds are the test evidence; /simplify and /code-review exempt per the configuration-only class.
<!-- SECTION:FINAL_SUMMARY:END -->

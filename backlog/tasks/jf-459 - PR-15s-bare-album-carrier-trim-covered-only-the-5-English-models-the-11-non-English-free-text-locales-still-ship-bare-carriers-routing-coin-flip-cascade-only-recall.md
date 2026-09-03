---
id: JF-459
title: >-
  PR #15's bare-album-carrier trim covered only the 5 English models; the 11
  non-English free-text locales still ship bare carriers (routing coin flip +
  cascade-only recall)
status: Done
assignee: []
created_date: '2026-09-03 04:28'
updated_date: '2026-09-03 06:43'
labels:
  - interaction-model
  - i18n
  - routing
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/pull/15'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-345 review gate (2026-09-03, score 75, finding 1). PR #15 trimmed PlayAlbum's bare-carrier samples from ONLY the five English free-text models (en-US/GB/AU/CA/IN; verified via gh api repos/paoloantinori/jellyfin-alexa-pr/pulls/15/files). The other 11 free-text locales STILL ship bare album carriers (verified in the working tree: de-DE 'Spiele {album}', es 'Reproduce {album}', fr 'Lis {album}', pt-BR 'tocar {album}', nl 'speel {album}', ar, ja, hi variants). Consequence: in those 11 locales a bare album utterance is a routing coin flip between PlayAlbumIntent and PlaySongIntent (the collision class PR #15 fixed for English), while the JF-345 cascade only recovers the PlaySong-miss half. The task file's original '16 of 17 locales' framing conflated the free-text SLOT TYPE (true: 16/17 use AMAZON.MusicRecording) with the carrier trim (false: only 5 were trimmed). Decide: (a) extend the trim to the 11 locales (mirror PR #15's shape per locale: remove bare 'play {album}' style samples from PlayAlbumIntent in de/es/fr/pt/nl/ar/ja/hi models; the JF-345 cascade then owns bare album requests uniformly), requiring NLU fixture updates and cross-locale validation, or (b) leave the carriers (they do not break anything: routing coin flip plus cascade recall) and close this as documented behavior. Reviewer's lean: (a), since the collision was judged worth fixing for English and the same wrong-artist-vs-album symptoms apply.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented option (a): PR #15's bare-album-carrier trim extended to the 11 non-English free-text locales (de-DE, es-ES/MX/US, fr-FR/CA, hi-IN, ja-JP, nl-NL, pt-BR, ar-SA). 113 PlayAlbumIntent samples removed (single-slot bare carriers, {album}+{musician} bare forms, stream carriers); every locale keeps its noun-carrying and indefinite forms (min ja-JP = 4). Bare verb+title now routes deterministically AWAY from PlayAlbum in all 16 free-text locales (to PlaySong in 13; in the es locales bare Reproduce/Pon is captured by PlayByGenreIntent's bare genre carriers instead, see JF-463). Album recall is preserved by the JF-345 cascade on a confirmed song miss.

Mirrors updated same-commit: fr-CA fixture (swap to probe-verified "Mets le disque abbey road", JF-399 copy-paste duplicate removed, block relocated under the PlayAlbum banner, divergence pointer corrected to JF-406), VOICE_COMMANDS.md (213 dead tokens removed across all 17 rows against model ground truth, repairing the PR #15 English orphans and pre-existing it-IT drift), 11 playback-lifecycle md edge labels, docs+docs-site graphs.json (targeted label edits, mirrors kept identical), docs-site/data.json. CLAUDE.md gains anti-pattern #11 (rule, tested detection snippet, six-mirror update list).

Deployed to minix (DLL verified active: size + embedded markers), models rebuilt per-locale on Amazon (de-DE, es-ES/MX/US, fr-FR/CA + en-US; the rebuild endpoint is scoped to CustomModelLocale so each locale was passed explicitly; saved models verified trimmed via get-interaction-model). Live matrix: profile-nlu probes, all JF-459 invariants held (bare not PlayAlbum in de/es, nouned still PlayAlbum, fr-CA divergence now fr-FR-like, en-US control unchanged). Suite 3056/3056, Release build 0 warnings, validators PASS.

Gates: /simplify (4 angles, findings applied); review via the review-local skill (5 parallel reviewers + scoring; the bundled code-review skill is disable-model-invocation in this environment) PLUS a final pr-review-toolkit:code-reviewer pass on commit 20c4db36 whose single finding (P3@88, determinism claim over-scoped to all 16) was fixed in follow-up commit 74ca4d6c. Commit messages carry the Gates marker.

CORRECTION recorded for the JF-459 commit message (immutable history): its "routes to PlaySong deterministically in all 16 free-text locales" sentence over-claims; the accurate statement is the 13-of-16 scoping above (JF-463 evidence).

Follow-ups filed from the gates: JF-460 (validator warning + fixture lint), JF-461 (BrowseCategory ids outside English locales), JF-462 (docs graph JSONs lag md sources; parse_mermaid.py drops targets), JF-463 (es bare verb+title captured by PlayByGenreIntent, discovered by the live matrix). A suggested loop-sample trim was refuted at model level (every non-English LoopSongOn sample carries the song noun).
<!-- SECTION:FINAL_SUMMARY:END -->

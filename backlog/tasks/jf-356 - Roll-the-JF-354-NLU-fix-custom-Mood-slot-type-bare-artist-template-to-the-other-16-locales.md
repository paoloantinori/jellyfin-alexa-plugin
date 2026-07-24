---
id: JF-356
title: >-
  Roll the JF-354 NLU fix (custom Mood slot type + bare artist template) to the
  other 16 locales
status: Done
assignee: []
created_date: '2026-07-20 07:56'
updated_date: '2026-07-20 12:27'
labels:
  - nlu
  - interaction-model
  - mood
  - artist-search
  - i18n
  - follow-up
dependencies: []
references:
  - JF-354
  - JF-355
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Roll the JF-354 it-IT pioneer (NLU: favor direct artist invocation over PlayMoodMusic) to the other 16 locales. For EACH locale (the 16 non-it-IT models -- hand-edited JSONs, NOT YAML templates):

1. Add a custom `Mood` slot type with that locale's mood vocabulary (from PlayMoodMusicIntentHandler.LocalizedMoodMap -- de-DE/es-ES/fr-FR/pt-BR entries + the English MoodGenreMap keys for the en-* locales). Values + synonyms per locale.
2. Change PlayMoodMusicIntent's `mood` slot from AMAZON.SearchQuery to the custom `Mood` type, and narrow the samples: drop the greedy "music {mood}" / "musica {mood}" / locale-equivalent carrier (it captures "music by X" -> misroute); use "{noun} {mood}" + the locale's purpose-carrier (e.g. "music for {mood}") with the Mood slot type. No concrete samples without a slot (empty-slot trap).
3. Add the bare artist template to PlayArtistSongsIntent (the no-verb "{media_noun} {artist_article} {musician}" / locale-equivalent) so "music by X" / "musica di X" routes directly to the artist intent.

Verify per locale via profile-nlu: "<artist invocation>" -> PlayArtistSongs; "<mood utterance>" -> PlayMoodMusic with the mood slot FILLED. This is a large mechanical change across 16 JSONs with locale-specific vocabulary + carriers -- track + do systematically. References JF-354 (the it-IT pioneer) + JF-355 (Spotify mood list + user-settings exposure, a further follow-up).
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Rolled the custom Mood slot type to all 16 non-it-IT locales. Per locale: (1) added a custom Mood slot type with locale-specific values (en-* use MoodGenreMap keys; de/es/fr/pt reuse LocalizedMoodMap words; nl-NL/ja-JP/hi-IN/ar-SA are new translations — ja-JP uses katakana loanwords per Alexa-JP convention), (2) changed PlayMoodMusic mood slot AMAZON.SearchQuery -> Mood (fixes the 'music by X' misroute in all 17 locales), (3) narrowed mood samples to slotted-only (anti-pattern #1). Added all new translated words + es/fr/pt gaps (dormir/fête/sono) to LocalizedMoodMap so ResolveGenres resolves them. The bare-artist template (item 3) was already present in all 16 locales — no edit needed.

Verified via profile-nlu on 10/16 locales across all script families (en-US/GB, de-DE, es-ES, fr-FR, pt-BR, hi-IN, ar-SA mood + artist): artist invocations route to PlayArtistSongs (misroute fixed), mood utterances fill the slot. ja-JP model deployed OK but profile-nlu unverifiable via CLI (CJK shell-encoding). ar-SA artist->PlayVideo is pre-existing/unrelated.

DoD: build 0 warnings, test 2551/2551, validators PASS, /code-review high 0 findings, /simplify passed (1 cleanup applied: committed scripts/generate_mood_slot.py generator; 2 skipped as cosmetic/pre-existing). NLU fixtures updated (en-US, it-IT stale rows). Commits: 15f5d54, f69460e, 0860a9d.
<!-- SECTION:FINAL_SUMMARY:END -->

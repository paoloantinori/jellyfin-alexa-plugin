---
id: JF-814
title: >-
  JF-814 - PlayEpisodeIntent lacks season-less samples: the natural phrase
  "l'episodio N di X" routes to PlayNextEpisode and fuzzy-matches a wrong item
status: To Do
assignee: []
created_date: '2026-10-08'
labels:
  - nlu
  - bug
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the 2026-10-08 device-round incident (live evidence, profile-nlu
confirmed): the natural one-shot "chiedi a mia collezione di mettere l'episodio
54 di Sailor Moon" selects **PlayNextEpisodeIntent** (series_name="54 di sailor
moon"), not PlayEpisodeIntent - EVERY PlayEpisode sample in the it-IT model (and
per the template family, all 17 locales) requires the season number. The
PlayNextEpisode handler then fuzzy-resolves the polluted slot and matched
"Sailor Moon R - The Movie" (the wrong item), minting a VideoApp launch for it.

Fix shape: add season-less PlayEpisode samples to the it-IT template (and the
16 mirrors): "metti l'episodio {episode_number} di {series_name}" plus the
imperative/infinitive/one-shot-wrapper family. The handler already elicits the
season when the slot is missing (verify: season_number elicit path exists -
the elicitation flow must catch the season-less match and ask "quale stagione?"
before querying). MIND the NLU competition rules (anti-pattern #3): the new
samples must not steal from PlayNextEpisodeIntent's legitimate phrases
("l'episodio successivo/prossimo di X") - run the nlu-verify battery over both
intents' phrases in the 17 locales before deploying.

Evidence: profile-nlu 2026-10-08 selectedIntent=PlayNextEpisodeIntent for the
exact user utterance; the handler log shows the wrong-item fuzzy match and the
VideoApp launch for 'Sailor Moon R - The Movie'.
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

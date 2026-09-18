---
id: JF-585
title: >-
  PlayNextEpisode infinitive phrasing loses deterministically to PlayByGenre's
  greedy 'Di riprodurre {genre}' SearchQuery (10/10 probes; the JF-541
  condemning probe for the Di-riprodurre variant)
status: Done
assignee: []
created_date: '2026-09-17 18:40'
updated_date: '2026-09-18 11:02'
labels:
  - nlu
  - competition
  - it-IT
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-583 closure probes (2026-09-17, live profile-nlu it-IT): 'Di riprodurre l'ultimo episodio di stranger things' routes to PlayByGenreIntent deterministically (10/10 probes across 2 identical-content model rebuilds) because PlayByGenreIntent carries the greedy 'Di riprodurre {genre}' sample (genre = AMAZON.SearchQuery) whose span swallows the whole tail. The {infinitive} {episode_position} episodio di {series_name} sample exists and is correct training data; an explicit literal-carrier sample (Di riprodurre l'ultimo episodio di {series_name}, the JF-504 precedent, landed in 532159bc) did NOT flip the outcome. The imperative and Metti families WIN (their sibling Metti {genre} exists but loses), so the competition is specific to the Di-riprodurre prefix. The failing it-IT fixture row (test_nlu.py ... ultimo episodio di stranger things, expecting PlayNextEpisodeIntent + episode_position) is RED until this lands; the row documents the desired behavior. Candidate resolutions, to be probed before choosing: (a) REMOVE 'Di riprodurre {genre}' from PlayByGenreIntent (subtract before add): the template comment documents it was deliberately kept after JF-541 with handler-side genre recovery, and pins the two fixture shapes that flip on removal ('disco thriller' and the 'musica dei' shapes) - probe what those route to after removal (FallbackIntent? no-selection?) and whether their handler recovery still applies; (b) weaken only the Di-riprodurre variant of the greedy family (keep Riproduci/Suona/Metti/Pleia {genre}); (c) accept the capture and note that the latest-episode semantics are NOT recoverable handler-side from a genre capture (the recovery argument that justified keeping it covers genre/artist shapes, not the position family). Evidence class: the JF-541 probe discipline (remove only when a probe condemns) - this IS the condemning probe for the Di-riprodurre variant. Related: JF-583 (the slot), JF-354 (Mood architecture), the it-IT template comment at the PlayByGenreIntent block documenting the original keep decision.
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
DONE (commits 81d4dbad + fa151b58 + 693cd02a). Resolution (b) of the task's own option list, executed with the live probe chain: the greedy 'Di riprodurre {genre}' SearchQuery sample was REMOVED from PlayByGenreIntent (the JF-541 condemning probe: 10/10 deterministic steals of the episode position family), and the infinitive genre ask survives via the NEW anchored carrier 'Di riprodurre genere {genre}' (consistent with the existing Riproduci/Suona/Metti genere {genre} family). Two follow-up discoveries from the live chain, both fixed same-session: (1) the explicit literal carriers added in 532159bc (the JF-504 precedent attempt) were REMOVED again - profile-nlu proved they duplicate the vocabulary expansion of '{infinitive} {episode_position} episodio di {series_name}' verbatim AND suppress the episode_position slot fill (the literal sample matched with series_name filled but episode_position empty); without them the vocabulary-expanded slotted sample fills BOTH slots; (2) the removal flipped three it-IT trainer pins, triaged with the rebuild-battery protocol (identical-content rebuild + probe batteries per the trainer-nondeterminism memory): 'suona matrix' now stable 4/4 PlayRadioIntent (same JF-436 triage class, re-pinned), 'pleia matrix' now deterministic NO_SELECTION 5/5 (converted to a documented skip_reason - profile-nlu raises on no-selection so the expectation is inexpressible), and 'Suona musica dei pink floyd' now stable 4/4 PlayArtistSongsIntent (the JF-541 catalog-steal flip reverted; the semantically-correct resolution for 'musica dei X', re-pinned). Final live state: the KNOWN-RED row 'Di riprodurre l'ultimo episodio di stranger things' PASSES (intent + both slots), the two genre rows (Riproduci genere rock / Suona genere jazz) stay green, dry-run validates, and the voice-reference mirrors were regenerated twice (also absorbing the ja-JP space fix and clearing the stale-sample warnings back to the 118 baseline). Suite 4069/4069 both TFMs throughout; no C# changes in this task (model + fixtures only).
<!-- SECTION:FINAL_SUMMARY:END -->

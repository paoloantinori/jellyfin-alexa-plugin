---
id: JF-506
title: >-
  Dot session follow-ups: pre-warm the session cache on LaunchRequest
  (first-intent 'utente non trovato' window) + 'cerca la canzone X' in-session
  misroutes to SearchMediaIntent as an artist query
status: In Progress
assignee: []
created_date: '2026-09-06 15:14'
updated_date: '2026-09-07 03:51'
labels:
  - ux
  - nlu
  - session
dependencies: []
references:
  - corr=ce5cb86a
  - corr=3240220d
  - JF-477
  - FindSongIntent
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two smaller findings from the 2026-09-06 Dot session. (A) Session pre-warm on LaunchRequest: a brand-new device's FIRST intent hits the JF-477 2s fast-fail budget and answers 'Utente non trovato. Per favore ricollega il tuo account' (17:08:45 corr=ce5cb86a), self-healing ~6s later when the warm-fill lands (17:08:51); his in-window retries also failed. Improvement: on LaunchRequest, fire the session lookup fire-and-forget so the cache is warm before the first real intent (the 'apri mia collezione' launch almost always precedes the command). (B) 'cerca la canzone screenwriter's blues' in a freshly-reopened session (after the resume-offer NoIntent cleared FindSongSessionData) routed to SearchMediaIntent (17:11:30 corr=3240220d), which ran an ARTIST search on the whole song title and not-founded ('Spiacente non ho trovato il contenuto'); FindSongIntent never saw it. Investigate the in-session intent competition for the 'cerca la canzone {titleKeywords}' carrier (samples? the FindSong controller force-route only applies with FindSongSessionData present) and fix the routing or add disambiguating samples so the song-search shape wins.
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

## Implementation Notes (2026-09-07, worker session)

### Evidence pulled (minix log_20260906.log)

Part A (corr=ce5cb86a): the device's FIRST request ever (17:08:43.760) was an
`IntentRequest` `AMAZON.FallbackIntent` sessionNew=True; NO LaunchRequest
preceded it in the log (first occurrence of the device id is that request). The
lookup blew the 2s fast-fail (17:08:45.767, "degrading to the not-found
response"), answered "Utente non trovato", warm-fill landed 17:08:51.943
(~8.2s after start; the first EF read hung past the whole 6s retry budget).
Next request 17:09:17 was a cache hit. No in-window retry requests appear in
the log (the narrative's retries never reached the skill).

Part B (corr=3240220d): 17:11:14 LaunchRequest (resume offer for a Ribs
episode) -> 17:11:21 NoIntent (resume rejection, FreshStart) -> 17:11:30
"cerca la canzone screenwriter's blues" routed to SearchMediaIntent
query="screenwriters blues": SearchTerm 0 results, 4-tier ArtistSearch 0
results, first-500-rows fuzzy pass null -> "Spiacente, non ho trovato il
contenuto." One minute earlier the same session's PlaySong n-gram fallback had
matched 100 songs for a FUZZIER query ("i soul coffin"), proving the song
pipeline finds what SearchMedia's chain misses.

profile-nlu on the deployed it-IT model (clean string): "cerca la canzone
screenwriter's blues" -> SearchMediaIntent (reproduced). Competition map:
definite-article no-chiamata shapes ("cerca/la canzone/il brano", "trova la
canzone") ALL select SearchMediaIntent; indefinite ("cerca una canzone X",
"trova una canzone X") and chiamata shapes select FindSongIntent.

### Part A shape (implemented)

`BaseHandler.WarmSessionCacheFireAndForget(token, deviceId)` +
`PreWarmSessionCacheAsync` (full-budget lookup via the extracted
`StartSessionLookup`, the ONE JF-477 Task.Run dispatch shape now shared with
`ResolveSessionAsync`), fired at `LaunchRequestHandler.HandleAsync` entry.
No-op on empty token/device (the JF-477 null-token unfiltered-query trap) and
on cache hit; self-catching; RunFireAndForget.

Honest wiring note: in the CURRENT production wiring `HandleRequestAsync`
already stores the session before HandleAsync runs, so the pre-warm no-ops on
the happy launch path and never fires on the fast-fail path (HandleAsync is
not reached when the session is null; that path already dispatches the JF-477
warm-fill). Its value is the invariant guarantee at the handler seam (tests,
simulator, future refactors that move resolution), exactly as the task's test
spec describes (launch triggers pre-warm at the cold-cache seam, failure does
not break the launch, cache-hit does not re-warm). The incident's own request
was intent-first (no launch reached the plugin), so nothing plugin-side could
have prevented THAT specific failure beyond the existing warm-fill; the
pre-warm covers the normal launch-precedes-command pattern.

Tests: `SessionPreWarmLaunchTests` (4): cold-cache populates, lookup-throws
still answers + lookup attempted, cache-hit Times.Never lookup, empty-token
Times.Never lookup.

### Part B: chosen layer + rationale

Model layer (task option i+ii combined) + handler-side recovery (option iii),
it-IT ONLY:

1. Template/model (source of truth): removed SearchMediaIntent's 4 song-named
   carriers (Cerca/Trova il brano/la canzone {query}, added 2026-06-05 by
   65e7e811 to fix an UNROUTED phrase) and added the 4 plain definite-article
   carriers to FindSongIntent (cerca/trova la canzone/il brano
   {titleKeywords}). The wrong intent owned the exact-string sample and won
   every time; the song-named shape must route to the intent that owns the
   song-title pipeline. Regenerated model_it-IT.json (60 intents, 1435
   samples; net sample count unchanged, 4 moved).
2. Locale survey: it-IT is the ONLY locale whose SearchMediaIntent carries
   song-named carriers (verified across all 17 model JSONs); the other 16 have
   no such competition, so nothing to add there (per the task's "only where
   the gap exists").
3. Handler-side (iii, evaluated and IMPLEMENTED): SearchMediaIntentHandler's
   confirmed not-found path now runs a bounded song-title retry
   (`TrySongTitleRetry`) via the shared SongIndexSearch chain
   (SearchWithPhoneticFallback) before answering MediaNotFound. Rationale: the
   fuzzy pass scans only the FIRST 500 rows of the playable kinds (structurally
   blind to most of a large song catalog; the incident is the proof), the
   n-gram index is the O(1) complete lookup, and ANY generic carrier
   ("cerca il contenuto X", "hai un film X") can still deliver a song title
   after the model fix. Guards: FilterByContentAccess hard-zero when music is
   disabled (JF-466), SkillWarmingUpException caught (opportunistic fallback
   contract; a unified search must not degrade to a warming refusal), null/
   disabled index returns empty, results capped at MaxSearchResults and fed
   into the EXISTING result flow (single auto-play / fuzzy / disambiguation;
   no duplicated disambiguation logic).

Tests: 4 new in SearchMediaIntentHandlerTests (index hit plays the song,
index miss still not-found, warming degrades to not-found, music-disabled
skips the index Times.Never).

### Fixtures + mirrors

- tests/integration/fixtures/it-IT.yaml: +4 NLU cases (the incident phrase
  and trova-variant intent-only [apostrophe fill is Amazon-statistical, the
  JF-441/JF-490 lesson], "cerca il brano bohemian rhapsody" ->
  FindSongIntent with titleKeywords [the 65e7e811 phrase, regression guard],
  and a negative guard "Cerca il contenuto bohemian rhapsody" ->
  SearchMediaIntent).
- tests/integration/fixtures/e2e_it-IT.yaml: "cerca il brano bohemian
  rhapsody" expectation SearchMediaIntent -> FindSongIntent (the stale
  expectation the CLAUDE.md mirror rule warns about).
- VOICE_COMMANDS.md (it-IT Search Media row) and
  docs/VOICE_COMMANDS_BY_LOCALE.md (it-IT sections) updated; docs graphs
  contain no references to the moved samples (checked).

POST-DEPLOY NOTE (orchestrator): the 4 new NLU fixture cases + the updated e2e
case can only be confirmed against the LIVE model after the it-IT model deploy
(profile-nlu today still shows the old routing; the no-deploy constraint is
per task rules). Run ./scripts/run_nlu_tests.sh -k "it-IT" after deploying.

### Verification (this session)

- dotnet build: 0 errors, 0 warnings.
- dotnet test: 3395 passed, 0 failed (suite grew past the 3381 baseline from
  intervening tasks; +8 new tests here).
- validate_interaction_models.py: PASS, 90 warnings (baseline).
- validate_locales.py: PASS. validate_versions.py: PASS (0.12.1.0).
- NLU dry-run: 8 passed. E2E dry-run: 141 collected.
- Gates: /simplify ran (2 fixes applied: StartSessionLookup extraction,
  not-found nesting flatten); code-review (review-local, in-session per the
  no-subagent rule) found nothing >= 80 (2 sub-80 notes: config-read idiom
  mirrors file precedent; fixture validation is inherently post-deploy).

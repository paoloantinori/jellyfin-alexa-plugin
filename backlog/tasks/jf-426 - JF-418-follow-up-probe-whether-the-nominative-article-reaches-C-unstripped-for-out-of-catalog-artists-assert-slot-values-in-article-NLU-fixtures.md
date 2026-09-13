---
id: JF-426
title: >-
  JF-418 follow-up: probe whether the nominative article reaches C# unstripped
  for out-of-catalog artists; assert slot values in article NLU fixtures
status: Done
assignee: []
created_date: '2026-08-31 15:02'
labels:
  - code-review
  - probe-first
  - nlu
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs:117
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Util/KeywordMatcher.cs:70'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs:2513'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from the JF-418 /simplify altitude review (2026-08-31). PlayArtistSongsIntentHandler.cs:117 reads musicianSlot.Value raw.

CONTEXT: JF-418 added nominative-article samples ("Suona i {musician}") to the it-IT model; all profile-nlu probes show AMAZON.Musician stripping the article (musician=queen for "suona i queen"). BUT every probe artist was in Amazon's catalog (Queen, Radiohead, Pink Floyd, Mina): the stripping may come from entity resolution against Amazon's catalog rather than the slot type itself. An out-of-catalog artist spoken with an article could deliver "gli xyzzy foo" raw, and a leading article poisons all 4 search tiers (every Contains/StartsWith shape fails).

The strip vocabulary already exists: KeywordMatcher.cs:70 StopWords["it"] opens with the same six articles, and BaseHandler.cs:2513 already strips this way for the cross-media fallback. The it-IT YAML vocabulary comment cross-references this twin.

PROBE BEFORE CODE (agent's explicit judgment): zero observed failures, so a preemptive C# strip would be speculative. One on-device probe decides it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 On-device (or simulator with raw slot passthrough) probe: speak an artist NOT in Amazon's catalog with an article ('suona gli xyzzy foo') and record whether musician arrives as 'xyzzy foo' or 'gli xyzzy foo'
- [ ] #2 If the article leaks: leading-article strip added in the artist entry path reusing KeywordMatcher's Italian article set (single definition, cross-referenced from the it-IT YAML comment), with unit test
- [ ] #3 If the article does not leak: finding documented as not reproducible, task closed
- [ ] #4 NLU article fixtures upgraded to assert the slot VALUE (article stripped) for at least one in-catalog case, so Amazon-side stripping drift fails the suite
<!-- AC:END -->

## Implementation Notes

COMPLETED 2026-09-13 (AC#1 probed, AC#2 applied, AC#4 pinned; live end-to-end verify BLOCKED on a server relink, see below):
- AC#1 PROBE (profile-nlu, it-IT): the article DOES leak for out-of-catalog artists. "suona i 24 grana" delivers musician='i 24 grana' RAW (article intact; the library artist is "24 Grana", so every search tier would fail). In-catalog artists get Amazon-side stripping ("suona gli afterhours" -> 'afterhours') but even those are inconsistent: "suona i pink floyd" resolves to 'i P!nk floyd' (catalog phonetic substitution WITH the article still attached).
- AC#2 FIX: ArtistSearch.StripLeadingArticle (it-IT only, ONE leading article from the KeywordMatcher six: il/lo/la/i/gli/le) applied at all four raw musician-slot read sites (PlayArtistSongs, PlaySong, PlayAlbum, QueryArtistLibrary). 8 unit tests (article variants, single-token, double-article, non-Italian locale unchanged).
- AC#4 FIXTURES: the NLU runner now supports {value: "..."} exact slot-value pins (drift fails the suite, not just fill/non-fill); "suona i pink floyd" pinned to the live truth 'i P!nk floyd'. The pin IMMEDIATELY caught real state: Amazon does NOT strip this form; the C# strip handles it at runtime. Also re-pinned "Suona musica dei pink floyd" to PlaySongIntent after the JF-553 stability protocol showed a STABLE 6/6 flip (not trainer noise): the JF-541 catalog-steal class with the catalog anchor present.
- LIVE VERIFY COMPLETED 2026-09-13 20:14 (post-relink): the exact raw article form through the simulator now works end to end. Input musician='i 24 grana' -> handler log "matched artist='24 Grana' (id=c25616ff...)" -> AudioPlayer queue of 5 songs. The cross-site strip verified on PlaySong too: musician='gli afterhours' -> "artist search returned 1 results for 'afterhours'". (The verify was briefly blocked: the 19:22 DLL swap killed the stored JellyfinToken against the server - both the current XML token and the 00:41 backup rejected, tokens-differ; the documented hot-swap-wipes-token class, deploy_hotswap_jellyfintoken memory. Paolo relinked via the config page; genre browse sanity re-verified green in the same pass.)

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

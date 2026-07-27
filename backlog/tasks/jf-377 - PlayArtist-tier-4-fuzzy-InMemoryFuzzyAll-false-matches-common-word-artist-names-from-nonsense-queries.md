---
id: JF-377
title: >-
  PlayArtist tier-4 fuzzy (InMemoryFuzzyAll) false-matches common-word artist
  names from nonsense queries
status: Done
assignee: []
created_date: '2026-07-25 17:57'
updated_date: '2026-07-27 04:33'
labels:
  - bug
  - artist-search
  - fuzzy-match
  - search-quality
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/ArtistSearch.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
During Koop investigation 2026-07-25, the PlayArtist tier-4 fuzzy search (InMemoryFuzzyAll) matched a literal artist named 'artist' from the nonsense query 'zzzqqq nonexistent artist' and auto-played it. Reproduced via simulator: corr=8799e4e2, ArtistSearch tier=4 method=InMemoryFuzzyAll matched=True results=1, matched artist='artist' (id 6ed4179f-2c58-5635-bdc8-9494c581d846), played 'Track 09'.

This is a latent false-positive: the fuzzy-all tier is too permissive and can auto-play an unrelated artist when the query contains a common word that happens to be an artist name. Not the originally-reported Koop bug (the handler finds Koop correctly), but a real quality issue surfaced while debugging.

LIKELY AREA: BaseHandler.ArtistSearch tier-4 (Alexa/Util/ArtistSearch.cs) + the auto-play threshold in HandleFuzzyMiss. Compare to the word-count guards already used in the cross-media fallback (CrossMediaArtistMaxWords=2).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce: query PlayArtistSongs with a multi-word nonsense string containing a common word like 'artist' (e.g. 'zzzqqq nonexistent artist'); confirm tier-4 InMemoryFuzzyAll matches the literal artist named 'artist' and auto-plays, a false positive
- [ ] #2 Investigate the tier-4 InMemoryFuzzyAll scoring/threshold: why does a 3-word nonsense query score above the auto-play threshold against a single common word like 'artist'
- [ ] #3 Decide on a fix: raise the tier-4 threshold, add a word-coverage/length guard (similar to CrossMediaArtistMaxWords), or require a minimum match score for auto-play on the fuzzy-all tier
- [ ] #4 Regression: verify a real near-miss artist query still resolves (don't break the intended fuzzy recall), and the nonsense query no longer false-matches
- [ ] #5 Live verify on minix: the false-positive case returns a clean not-found, and a legitimate artist still plays
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
FIXED 2026-07-27 with a disambiguation DOWNGRADE (not a reject), after exhaustive research proved a pure reject is not viable.

ROOT CAUSE (verified live on minix,corr=8799e4e2 confirmed): a nonsense query "zzzqqq nonexistent artist" auto-plays the literal artist "artist" because FuzzyMatcher.PartialRatio has a containment shortcut (if one string contains the other, score jumps to ContainmentScore=90 >= the single-match auto-play path). The auto-play was NOT a HandleFuzzyMiss decision - when tier-4 returns exactly 1 artist, PlayArtistSongsIntentHandler skips HandleFuzzyMiss (runs only at count>1) and plays artists[0] unconditionally.

THREE REJECT ATTEMPTS FAILED (all discarded): a tier-4 word-coverage reject guard was reverted in /code-review high because it also rejects REAL artists when carrier phrases bleed into the raw musician slot value (e.g. "suona la musica di bush" -> "Bush" rejected). Stop-word stripping (KeywordMatcher.Tokenize) only removes articles/prepositions, NOT carrier verbs/nouns (suona, musica), so it cannot separate the cases.

RESEARCH (claudedocs/research_jf377_discriminator_2026-07-26.md, exhaustive, primary Amazon docs + IR entity-linking literature): the bug case and the regression case are STRING-INDISTINGUISHABLE by coverage/length/frequency. Amazon's own docs define carrier phrases as "the word or words that are part of the utterance, but not the slot" - so carrier bleed into the slot is an NLU-failure tail case, not the common path. The established mitigation for an ambiguous entity match (entity-linking literature) is NOT silent reject, it is downgrade-to-confirmation.

FIX (shipped): ArtistSearch.IsCoincidentalContainmentMatch(query, candidateName, locale) predicate + a PlayArtistSongsIntentHandler branch: when artists.Count==1 AND the match is coincidental-containment, return DisambiguationHelper.AskFirstMatch (yes/no "Did you mean X?") instead of auto-playing. Real artists still play via "yes"; nonsense resolves to not-found via "no". KEY PROPERTY: NO regression (one extra turn for the ambiguous case is the deliberate trade). YesIntent routes MediaTypeArtist -> PlayArtist.

VERIFICATION: 2633 unit tests green, Release build clean, /simplify clean (4 agents), /code-review high clean (5 agents, 3 doc-comment defects fixed, no blocking correctness findings). Live-verified on minix: nonsense -> disambig prompt; "suona la musica di bush" -> disambig prompt (Bush reachable via yes, NO regression); "radiohed" -> auto-play; "soul coughing"/"bush" bare -> auto-play. Deployed to active 0.11.2.0 DLL (verified by identifier).

SCOPE LIMITATION (filed as JF-382): the downgrade only covers PlayArtistSongs count==1. The same coincidental-containment shape still ships through PlayArtistSongs count>1 paths and 12 other ArtistSearch.SearchAsync callers (cross-media fallbacks). Lower priority - file when a user reports the variant.
<!-- SECTION:FINAL_SUMMARY:END -->

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

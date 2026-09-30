---
id: JF-684
title: >-
  JF-684 - catalog musician slot blocks intent selection for non-catalog values:
  bare artist names produce NO intent (fuzzy tiers voice-unreachable)
status: In Progress
assignee: []
created_date: '2026-09-30 16:53'
labels:
  - catalog
  - interaction-model
  - routing
  - bug
dependencies: []
references:
  - >-
    backlog/tasks/jf-666 -
    JF-666-artist-plays-stop-at-the-initial-5-track-page-the-precompute-fast-path-starves-the-continuation-and-the-fetchers-JF-358-query-shape-silently-returns-zero.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn, live-verified twice (morning device round: 3 attempts, zero requests; evening: profile-nlu A/B proof at Paolo's suggestion).

THE EVIDENCE (profile-nlu, it-IT, skill amzn1.ask.skill.33dfacd5):
- "suona la musica di pink" -> selectedIntent: NONE (no intent at all)
- "suona la cantante pink" -> NONE (the carrier form does not help)
- "suona la musica di norah jones" -> PlayArtistSongsIntent, musician resolves (exact catalog value)
- "suona la musica di pink floyd" -> PlayArtistSongsIntent (exact catalog value)

THE MECHANISM: PlayArtistSongsIntent's musician slot is the CATALOG-BACKED type JellyfinArtist (valueSupplier.valueCatalog, fed by CatalogSyncTask from the user's library + phonetic synonyms). When the spoken value matches NO catalog value (bare "pink" is not a synonym of any entry), the NLU does not select ANY intent for the utterance: on-device this surfaces as "nothing happens" (zero requests to the endpoint; verified in logs across 4 attempts). This REFINES the JF-642 finding: static custom types restrict only ER resolution (slot filling still captures raw text), but CATALOG types (valueSupplier) constrain NLU intent SELECTION itself when no value matches.

THE BIG CONSEQUENCE: the entire artist fuzzy-recall machinery (the 4-tier chain, ASR-truncation shapes like "crash" -> Crash Test Dummies, the JF-377/JF-420 containment gates) is UNREACHABLE BY VOICE for any spoken name that does not already match a catalog value: the request never arrives. The fuzzy tiers only fire for catalog-ADJACENT inputs (synonym drift like "pink floid") but not for partial/truncated names.

FIX DIRECTIONS TO EVALUATE (decision task, needs the catalog-generator knowledge):
(a) Catalog-side partial synonyms: CatalogSyncTask adds first-word/dropped-article synonyms for multi-word artist names ("pink" -> Pink Floyd's entry, "crash" -> Crash Test Dummies) gated on distinctiveness (length >= N, not a stop word in any locale, unique among the library's first words) - "the" and "led"-class words must never become synonyms. Risk: a bare word that prefixes TWO artists ("miles" -> Miles Davis + Miles Kane) becomes a two-value synonym collision - acceptable: ER returns no match and the raw text still arrives, the handler fuzzy chain takes over (which is the whole point).
(b) Verify (a) actually unblocks selection: re-profile "suona la musica di pink" after adding the synonym; if catalog types still refuse selection on non-exact matches, (a) is dead and the only fix is moving the musician slot off the catalog type for PlayArtistSongs (a JF-96.2-architecture decision needing the CLAUDE.md anti-pattern 10 guardrails).
(c) Minimum viable: document the platform behavior (catalog types gate intent selection) in CLAUDE.md near the JF-642 note and in the JF-96.2 architecture notes.

VERIFICATION: profile-nlu A/B (before/after) on the failing corpus: "pink", "crash", "beatles" (beatles presumably already works via the dropped-article synonym - verify), plus a two-artist prefix collision case; the existing NLU fixture suite; the e2e artist matrix unchanged.
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

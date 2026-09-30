---
id: JF-690
title: >-
  JF-690 - shared-first-word catalog synonyms auto-play Amazon's top ER rank with
  no disambiguation prompt
status: To Do
assignee: []
created_date: '2026-09-30 20:40'
labels:
  - catalog
  - routing
  - ux
dependencies:
  - JF-684
references:
  - >-
    backlog/tasks/jf-684 -
    JF-684-catalog-musician-slot-blocks-intent-selection-for-non-catalog-values-bare-artist-names-produce-NO-intent-fuzzy-tiers-voice-unreachable.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn by the JF-684 worker from the /code-review high gate
(hand-created in the worker worktree per the number reserve; max existing was JF-689).

JF-684 added bare first-word synonyms to ARTIST catalog entries, which restores intent
selection for partial names. Live spike evidence (2026-09-30, it-IT, spike version
1197): "suona la musica di pink" returns a MULTI-VALUE ER match, values [P!nk, Pink
Floyd] (both library artists carry a "pink"-matching form), with slotValue.value
already set to Amazon's rank #1 ("P!nk").

The residual behavioral gap, verified in code during the JF-684 review:
- `SlotValueHelper.GetCanonicalValue` reads only `authority.Values[0].Value.Name`
  (SlotValueHelper.cs, the ER_SUCCESS_MATCH branch).
- `PlayArtistSongsIntentHandler` feeds the canonical to the artist search verbatim
  (`musicianQuery = canonicalMusician ?? musician`, the JF-659 contract).
- The resulting exact-name hit auto-plays through the JF-420.1 exact-equality bypass.

So a user saying "pink" in a library holding both P!nk and Pink Floyd gets whichever
Amazon ranks first, silently. The JF-420.2 yes/no disambiguation prompt exists for
exactly this shape but only fires on the raw-text path (no canonical). Net behavior is
still a strict improvement over JF-684's pre-fix state (silence, no request at all),
which is why this was filed rather than blocking JF-684.

Evaluation directions (need a decision + spike):
- A multi-value read alongside GetCanonicalValue (e.g. GetCanonicalValues returning all
  matched names): when ER returns >1 value, drive the existing DisambiguateMultipleArtists
  flow instead of the verbatim canonical feed. Beware: ER multi-value matches also occur
  for ordinary synonym drift where one artist matches via two synonyms (check whether
  Amazon de-dupes per VALUE before prompting; the spike's [P!nk, Pink Floyd] was genuinely
  two distinct values).
- Or accept Amazon's ranking as the tiebreak (documented, zero work) if a device round
  shows the top rank is almost always the intended artist.

Related: JF-688 (post-sync live verification; add a shared-word probe to that round).
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

---
id: JF-696
title: >-
  JF-697 - the generic-music-word fallback misses article forms: song slot "la
  musica" skips the artist fallback and answers not-found
status: Done
assignee: []
created_date: '2026-10-01 17:30'
labels:
  - bug
  - intent-handling
  - routing
dependencies: []
references:
  - >-
    backlog/tasks/jf-684 -
    JF-684-catalog-musician-slot-blocks-intent-selection-for-non-catalog-values-bare-artist-names-produce-NO-intent-fuzzy-tiers-voice-unreachable.md
  - >-
    backlog/tasks/jf-690 -
    JF-690-multi-value-ER-matches-resolve-to-Values[0]-and-auto-play-through-the-JF-420.1-equality-bypass-without-any-yes-no-disambiguation-prompt.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-01 19:35 same-turn from Paolo's live device round (log-verified end to end).

THE EVIDENCE: "Alexa, chiedi a mia collezione di suonare la musica di pink" now ROUTES (the JF-684 catalog fix working: PlaySongIntent arrived 19:27:21 with musician="P!nk" ER-resolved, tier-1 found the artist in 1ms - P!nk exists in the library), but the handler answered "Spiacente, non ho trovato nessuna canzone chiamata la musica di P!nk": the song slot carried "la musica" (the carrier words with the Italian article), and PlaySongIntentHandler's generic-music-word fallback gate (line ~242) is GenericMusicWords.Contains(songQuery) - an EXACT match on "musica"/"music"/... without the article. "la musica" does not match, the fallback skips, the song search for "la musica" by P!nk finds nothing, and the not-found answers. The fallback exists for EXACTLY this shape (its doc: "when the song slot contains a generic music word (e.g. 'musica'/'music') but the musician slot has a valid artist").

THE FIX: normalize the slot value before the Contains - strip the leading Italian (and locale-family) articles (il/lo/la/i/gli/le/l'/un/una/un' and the equivalents the set's languages use), then match. Reuse or generalize the existing article-stripping machinery if the repo has it (check it-IT vocabulary handling / SlotValueHelper); the normalization belongs at the ONE gate site (or a small helper the set documents). Mind: the same leak class may affect the OTHER locales' carriers ("die Musik" de, "la musique" fr, "a música" pt...) - the fix should cover the article families of the GenericMusicWords languages, not just Italian.

PIN: PlaySong with song="la musica", musician=<in-library artist> -> the artist's songs play (the fallback fires); red proof: without the normalization, the not-found answers (today's shape verbatim). A per-locale theory over the article forms is cheap.

VERIFICATION: the new pin + the existing PlaySong/fallback suites green; full suite both TFMs. Live spot check: "suonare la musica di pink" on the device plays (P!nk or Pink Floyd per ER ranking, JF-690's no-prompt gap unchanged).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic (N/A: handler-internal gate change, no model/fixture surface; covered by unit pins in PlaySongGenericMusicWordFallbackTests)
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes
<!-- SECTION:IMPL:BEGIN -->
Landed in the worker worktree (single commit, not pushed): PlaySongIntentHandler.IsGenericMusicQuery + StripGenericMusicLeadingArticle, one leading article of the covered languages (en/it/de/es/fr/nl/pt + elisions l'/un') stripped for the GenericMusicWords gate membership test ONLY; the song search keeps the raw slot (article-titled real songs still match by title, pinned). Red proofs run and read at the reverted gate: ArticledLaMusica and ArticledLaMusique both flip to the not-found shape; the La Vie En Rose boundary pin stays green either way. DoD 4-5 N/A (no session attrs, no HttpClient); 6-8 N/A (no interaction model, fixture, or locale-string change). Gates: simplify + code-review high, same turn. Nothing cut at a cap; JF-698 reserve untouched.
<!-- SECTION:IMPL:END -->

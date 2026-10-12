---
id: JF-868
title: >-
  JF-870: broken-Italian disambiguation trailer ("E 2 in più.") + audiobook stop
  speaks "fermato il video" instead of the book line
status: To Do
assignee: []
created_date: '2026-10-12 15:43'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two wording defects from Paolo's 2026-10-12 device round (both evidenced in podman logs, corr ids in minix logs 17:37 and 17:39 era):

1. ARTIST DISAMBIGUATION TRAILER is broken Italian. Exact spoken text: "Ho trovato più artisti: pearl jam, Pearl Jam w, Pearl Jam w/ Pete Townshend. Vuoi il primo? Di' no per il successivo. E 2 in più." The trailer "E {0} in più." (it-IT.json line 81, key DisambiguateMultipleArtistsMore) is a literal calque of "And {0} more." appended as a standalone sentence; in Italian it does not parse. Fix it-IT to a natural form ("Ci sono altri {0} artisti." or equivalent that reads as a full sentence after the cycling instruction) AND audit the other 16 locale files for the same standalone-calque shape (de "Und {0} mehr."-class); fix where equally broken. This is a locale JSON edit (all locales share ResponseStrings keys); ResponseStringsTests ledger may need updating if it pins the string.

2. AUDIOBOOK STOP LINE says the wrong medium. Paolo stopped the audiobook (VideoApp HLS path) via "pausa": the skill correctly stopped it but spoke "ho fermato il video" (VideoStoppedByVoice). Since JF-868 the VideoApp-family pause/cancel arm is unconditional, which is right for behavior, but the SPOKEN LINE must be medium-aware: books need their own key (BookStoppedByVoice-class, e.g. "Ho fermato il libro.") used when the resolved medium is the audiobook branch (the medium is already resolved in the handler via the JF-564 family discriminator; Live TV can keep the video line or get its own if cheap). 17 locale strings + ResponseStringsTests ledger entry + flip the JF-868-era test pins that assert VideoStoppedByVoice on the audiobook fixtures (VideoAppGapHonestResponseTests has audiobook pause pins from JF-564) to the new book line.

Both changes are string/branch-level; no model changes. Standard gates (/simplify, /code-review high, tests both TFMs). Deploy rides the next batch after JF-869's model fix to avoid two model-touching deploys.
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

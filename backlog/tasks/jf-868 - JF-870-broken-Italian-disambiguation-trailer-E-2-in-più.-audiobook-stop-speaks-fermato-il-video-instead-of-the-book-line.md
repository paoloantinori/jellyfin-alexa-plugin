---
id: JF-868
title: >-
  JF-870: broken-Italian disambiguation trailer ("E 2 in più.") + audiobook stop
  speaks "fermato il video" instead of the book line
status: Done
assignee: []
created_date: '2026-10-12 15:43'
updated_date: '2026-10-12 16:51'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-10-12 execution note (branch jf870, worktree /tmp/wt-jf870, merged 0adfd187): both defects fixed. Trailer: it-IT now "Ne ho {0} in più." (NOT the task's example "Ci sono altri {0} artisti.", deliberately: the JF-729 count-invariance rule in DisambiguationHelper's doc says the hint must stay grammatical at {0}=1, the MOST COMMON overflow, and "Ci sono altri 1 artisti" would speak "uno artisti"; the chosen form is grammatical at every count and Paolo's one-line preference wins if he accepts the 1-edge). Same-class verb-less calques also fixed: fr "Il y en a {0} de plus.", de "Es gibt noch {0} mehr.", nl "Ik heb er nog {0}."; kept as already natural: en (native appositive), es/pt (verb-carrying), ja (full sentence), hi/ar (documented invariant forms); the audit decision is recorded in the AskMultipleArtists doc comment. Book line: BookStoppedByVoice added to all 17 locales next to VideoStoppedByVoice (it "Ho fermato il libro.", en "I've stopped the book.", de Hörbuch, nl luisterboek, ja オーディオブック), PauseIntentHandler's VideoApp arm picks it on PlayingMedium.VideoAppAudiobook (Live TV and VideoAppAudio keep the video line; the seek-mode music gap is filed as JF-873; the worker's transient JF-871 numbering was corrected - JF-871 is the sunny/video-cascade task). Ledger row added; the audiobook pause pin flipped to a Pause+Cancel Theory via the shared AssertStoppedByVoiceTell core.

DoD waivers: #6 N/A (no interaction-model change; templates/models/NLU fixtures untouched per the dispatch). #7 waived for this change: an E2E case for the book stop line would need a live VideoApp audiobook playing on the device ledger, which the simulate-skill harness cannot set up deterministically (each fixture runs session-independent); the handler-logic coverage is the unit Theory, and the medium-classifier leg is already pinned by the JF-858 corroboration suites. #4/#5 untouched surfaces (no session-attribute or HttpClient change in this diff).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf870 (fe0a2aad), merged to main 0adfd187, pushed; deploy rides the next batch after JF-869. Defect 1: it-IT trailer "E {0} in più." is now "Ne ho {0} in più." - count-invariant and grammatical at {0}=1 (the most common overflow), which the suggested "Ci sono altri {0} artisti." is not ("altri 1 artisti" breaks agreement); the divergence is recorded in DisambiguationHelper.AskMultipleArtists's doc and is Paolo's final call. Same calque fixed in fr-FR/fr-CA ("Il y en a {0} de plus."), de-DE ("Es gibt noch {0} mehr."), nl-NL ("Ik heb er nog {0}."); en/es/pt/ja/hi/ar audited and left (already natural verb-bearing forms). Defect 2: BookStoppedByVoice added to all 17 locales; the VideoApp-family pause/cancel arm speaks it for PlayingMedium.VideoAppAudiobook (books stop saying "ho fermato il video"); video, Live TV, VideoAppAudio keep the video line. Ledger row added; the JF-868-era audiobook pin flipped to the book line and widened to a Pause+Cancel Theory. Gates: /simplify (4 agents; AssertStoppedByVoiceTell core hoist, 2 comment restatements removed; altitude verdict: inline ternary is the per-family idiom) + /code-review high (5 findings: 4 stale JF-868-era comments fixed, CannotPauseVideoByVoice retention reason recorded, trailer-audit keep decision recorded; E2E DoD item waived with rationale - simulate-skill cannot deterministically stage a playing VideoApp audiobook). Tests 5792/5792 BOTH TFMs; validate_locales PASS; Release 0/0. Residual FILED: seek-mode music (VideoAppAudio) keeps the video line on stop (JF-873).
<!-- SECTION:FINAL_SUMMARY:END -->

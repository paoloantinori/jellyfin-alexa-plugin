---
id: JF-620
title: >-
  Open-dialog trap swallows full one-shot commands with invocation name
  (song_query captured 'chiedi a mia collezione di attivare loop', answered with
  nonsense not-found)
status: In Progress
assignee: []
created_date: '2026-09-22 16:52'
updated_date: '2026-09-22 20:09'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-550 -
    Dead-mic-question-sweep-10-handlers-still-Tell-question-shaped-DidNotCatch-strings-with-shouldEndSessiontrue-same-class-as-JF-549-hoist-the-shared-cancel-hatch-leg-and-rename-FindSongCancelled.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-09-22 18:34 (battery test 9 collision): with the AddSongToPlaylist dialog open («Quale canzone vuoi aggiungere?» after «aggiungi canzone alla playlist prova echo»), the user spoke the next battery command as a full one-shot with invocation name («Alexa, chiedi a mia collezione di attivare loop»). Alexa delivered it INTO the open dialog as song_query="chiedi a mia collezione di attivare loop" (log corr=bc8cf7a7), the handler searched for a song with that name and answered «Spiaciente, non ho trovato nessuna canzone chiamata chiedi a mia collezione di attivare loop» - read by the user as "no songs playing / false". The JF-550 cancel hatch only escapes bare cancel words. Fix direction: in the shared BuildCancelDuringOpenElicit leg (or a sibling guard), detect a slot capture that embeds the invocation phrase (locale-aware invocation names from Config.LocaleInvocationNames/InvocationName, 'chiedi a <name>' / 'ask <name>' family) and treat it as an escape: end the flow with a short explanation (or, better, attempt to re-dispatch the embedded command through the intent the NLU would see - likely requires the AlexaController routing layer, so at minimum the escape-with-hint shape). Platform context: inside an open ElicitSlot session any utterance fills the slot; only explicit cancel words are guaranteed escapes, so plugin-side detection is the only lever we control.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 While a Dialog.ElicitSlot flow is open, a full one-shot with the invocation name («Alexa, chiedi a mia collezione di attivare loop») either executes as the new command or gets a clear spoken escape hint, instead of being swallowed as a slot value and answered with a nonsense not-found
- [ ] #2 The escape does not break legitimate slot answers that merely contain the word 'collezione' or similar
- [ ] #3 Unit test pins the invocation-phrase detection on the slot capture
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-22 23:05: SHIPPED (commit 8bb5e511, deployed to minix net10.0). Implementation: CancelWords.IsTrappedInvocationOneShot (locale ask-carrier AND invocation name, both diacritic-folded for unaccented ASR text; carrier-less ja/hi/ar require the name to LEAD the value with a command tail so a playlist named 'my collection' stays a real answer) + AnySlotIsTrappedInvocationOneShot on the shared AnySlot walk; BaseHandler.BuildCancelDuringOpenElicit gains the escape leg (ElicitTrapEscaped, 17 locales) and FindSong's own wider hatch gained the same disjunct (review K6: it does not use the shared leg). Candidates from Config.RuntimeInvocationNameCandidates (the first runtime invocation-name consumer; per-user custom name is a documented residual). Gates: simplify (equivalent pass 4/5 applied + literal 4-angle pass run as gate evidence), code-review high (10 findings: 9 applied incl. carriers gaps es/it/pt/fr + accent folding + name-only tightening; 1 documented), tests 4231/4231 x both TFMs. Device verification pending (user).
<!-- SECTION:NOTES:END -->

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

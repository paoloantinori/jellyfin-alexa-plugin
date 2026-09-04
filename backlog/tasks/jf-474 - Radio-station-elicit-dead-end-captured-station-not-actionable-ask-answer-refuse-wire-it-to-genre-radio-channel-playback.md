---
id: JF-474
title: >-
  Radio station elicit dead-end: captured station not actionable (ask -> answer
  -> refuse); wire it to genre radio / channel playback
status: To Do
assignee: []
created_date: '2026-09-03 16:28'
updated_date: '2026-09-04 11:37'
labels: []
dependencies: []
references:
  - JF-472 (the elicitation this completes)
  - JF-472 review finding 2 (ask-answer-refuse loop)
  - PlayChannel machinery (channel resolution)
  - FindRadioTracksAsync (genre radio seeding)
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from the JF-472 review (finding 2, confidence 90): the new radio station elicitation creates a dead-end loop. With nothing playing, the skill now asks "Quale stazione radio vuoi ascoltare?"; the user answers with a station/genre word ("jazz"); the captured station slot is not actionable (PlayRadioIntentHandler has no station/genre/channel playback on the nothing-playing branch), so the handler still answers RadioNothingPlaying and ends the session. Ask -> answer -> refuse: the prompt invites a reply that can only fail. The cancel-word escape (JF-423 hatch) works, but the happy path does not.

Feature decision needed (pick one):
(a) Station-to-channel: resolve the captured station against the live-TV radio channel list (the PlayChannel machinery: channel names like "jazz fm") and start that channel; fall through to (b) on no channel match.
(b) Station-to-genre radio: treat the captured word as a genre and seed RadioMode with that genre's tracks (the existing FindRadioTracksAsync machinery), i.e. make "suona jazz" -> (misrouted to radio) -> "jazz" answer -> genre radio work end-to-end. This ALSO mitigates the JF-472 Amazon-side half (bare genre forms stolen by PlayRadioIntent): even when Amazon steals the routing, the user still gets jazz.
(c) Extend (a)+(b) with a mood fallback (the MoodGenreMap).

Recommendation: (b) first (it reuses existing machinery, needs no new queries, and directly heals the device-reported case), (a) as an optional tier when a channel name matches exactly.

Acceptance criteria:
- Nothing playing + elicit + answer "jazz" -> genre radio starts (or a truthful not-found for words that are neither genre nor channel), never the RadioNothingPlaying Tell on a captured non-empty station.
- The JF-472 regression tests stay green (elicit, cancel hatch, something-playing path, empty-slot paths).
- Device re-verification item for Paolo: the full "suona jazz" -> answer "jazz" -> music plays chain.
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
UX REQUIREMENTS from Paolo's device test (2026-09-04, the elicit worked but was unanswerable): (a) the station prompt must OFFER choices: name 2-3 real options dynamically from the live-TV channel list (the 'radio'-named channels in the library, e.g. 'jazz fm', 'rtl'), with a genre-word fallback ('un genere come jazz o rock') when no channels exist; the reprompt (the second ask) is the natural carrier so the first ask stays short; (b) HELP PATH during the open elicit: the user's natural question 'quali ci sono?' currently lands in the station slot as free text and dead-ends; the station-given path must detect question-shaped answers (locale-aware help words: quali/cosa/elenco...) and respond with the available list + re-ask instead of the nothing-playing Tell.
<!-- SECTION:NOTES:END -->

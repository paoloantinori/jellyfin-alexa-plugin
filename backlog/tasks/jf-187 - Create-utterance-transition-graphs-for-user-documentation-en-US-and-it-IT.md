---
id: JF-187
title: Create utterance transition graphs for user documentation (en-US and it-IT)
status: Done
assignee: []
created_date: '2026-05-20 15:29'
updated_date: '2026-05-21 08:19'
labels:
  - documentation
  - ux
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create workflow/state-transition diagrams illustrating how users interact with the Alexa skill — what utterances trigger which states, what's active when, and how flows connect. The goal is a visual reference that helps users understand the full vocabulary and interaction patterns available to them.

### Scope
- **Locales**: en-US and it-IT first (other locales can follow the same pattern)
- **Format**: Mermaid.js diagrams (GitHub renders natively)
- **Location**: `docs/` directory

### Diagrams Needed

1. **Playback lifecycle** — Launch -> Play -> Pause -> Resume -> Stop, including now-playing info queries, seek/skip/jump operations (when SeekEnabled)
2. **Search & disambiguation flow** — "Play X" -> fuzzy match -> single result (auto-play) vs multiple results (disambiguation carousel: yes/no)
3. **Media info queries** — "What's playing?" -> slot-based follow-ups (title, album, artist, year, duration, genre, biography)
4. **Library browsing** — Browse intent, recently added, favorites, artist lookup, podcast/audiobook flows
5. **Queue & radio** — Add to queue, play next, radio mode on/off
6. **Session management** — Voice linking ("learn my voice"), follow-me, resume offer on skill re-launch

### Acceptance Criteria
- [ ] Mermaid diagrams render correctly on GitHub
- [ ] All intents from `model_en-US.json` and `model_it-IT.json` are covered
- [ ] Utterance examples use actual phrases from the interaction model samples
- [ ] Feature-gated flows (SeekEnabled, AplVisualsEnabled, AnnouncePositionOnResume) are annotated
- [ ] Italian diagrams use it-IT utterance samples, not translations of English ones
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created 12 Mermaid.js diagram files in docs/ covering 6 interaction flows (playback lifecycle, search/disambiguation, media info queries, library browsing, queue/radio, session management) for both en-US and it-IT. All 53 custom intents mapped. Feature flags (SeekEnabled, AplVisualsEnabled, ResumeOfferEnabled, AnnouncePositionOnResume) annotated with dotted lines. Italian diagrams use actual it-IT utterances from model_it-IT.json.
<!-- SECTION:FINAL_SUMMARY:END -->

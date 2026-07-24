---
id: JF-248
title: Implement FindSongIntentHandler with multi-turn conversational search
status: Done
assignee: []
created_date: '2026-06-03 19:12'
updated_date: '2026-06-03 20:16'
labels:
  - enhancement
  - search
  - handler
dependencies:
  - JF-245
references:
  - docs/superpowers/specs/2026-06-03-find-song-keyword-search-design.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create `Alexa/Handler/Intent/FindSongIntentHandler.cs` — a multi-turn intent handler implementing the conversational song search flow.

## Session State DTOs

```csharp
public enum FindSongState { AwaitingArtist, AwaitingKeywords, Disambiguating }
public record FindSongCandidate(Guid ItemId, string Name, string? ArtistName, double Score);
public class FindSongSessionData
{
    public FindSongState State { get; set; }
    public Guid? ArtistId { get; set; }
    public string? ArtistName { get; set; }
    public string? Keywords { get; set; }
    public List<FindSongCandidate>? Candidates { get; set; }
}
```

## CanHandle Logic

- `intent == FindSongIntent`, OR
- `session.FindSongState is set AND (intent == AMAZON.FallbackIntent OR intent == FindSongIntent)`
- Does NOT intercept YesIntent/NoIntent/StopIntent — those are handled by dedicated handlers. Stop/Cancel ends the session naturally, clearing state.

## State Machine

**First invocation (state null):**
- Has musician slot → resolve artist → state = AwaitingKeywords → prompt
- Has titleKeywords slot → state = AwaitingArtist → prompt
- Neither → state = AwaitingKeywords → prompt "What words do you remember?"

**AwaitingArtist:** Interpret transcript/Slot as artist name (reuse artist search). If found + keywords in session → search. If not found → FindSongArtistNotFound, keep state.

**AwaitingKeywords:** Extract keywords from slot or transcript. Tokenize via KeywordMatcher. If all stop words → FindSongTooVague. Otherwise → search.

**Disambiguating:** Match input to candidates by number ("the first one"), ordinal ("one", "two"), or partial title. If matched → play + clear state. If not → FindSongInvalidPick, keep state.

## Search Function

- If artist provided: `GetAllSongsByArtist(artist)` via ArtistIds filter
- If no artist: `SearchSongsByKeyword(tokens[0])` via NameContains DB query + KeywordMatcher post-filter
- Score with KeywordMatcher.Score, filter keywordCoverage == 1.0, take top 4
- 0 matches → FindSongNoMatch, keep state
- 1 match + score >= 90 → auto-play with FindSongFoundOne announcement
- 1-4 matches → state = Disambiguating, list candidates, prompt
- >4 matches without artist → FindSongTooManyNarrow, state = AwaitingArtist

## ShouldEndSession Rules

| State | ShouldEndSession | Reprompt |
|---|---|---|
| Prompts (Awaiting*) | false | locale-specific |
| Disambiguation | false | "Say the number or the title" |
| No match | false | "Try different words" |
| Playback | true | none |
| Too vague | false | "Try more specific words" |

## Registration

- Add `FindSongIntent` to `IntentNames.cs`
- Add `titleKeywords` slot to `SlotMappings.cs`
- Register handler in the pipeline

## Design Spec

`docs/superpowers/specs/2026-06-03-find-song-keyword-search-design.md`
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 FindSongIntentHandler handles all 4 states: null (first invocation), AwaitingArtist, AwaitingKeywords, Disambiguating
- [ ] #2 FindSongSessionData DTO stored in session attributes (no ValueTuples)
- [ ] #3 ShouldEndSession=false with reprompt for all prompt states; ShouldEndSession=true for playback
- [ ] #4 Artist-scoped search uses ArtistIds filter on InternalItemsQuery
- [ ] #5 Global search uses NameContains DB query + KeywordMatcher post-filter
- [ ] #6 Disambiguation resolves picks by number, ordinal, and partial title
- [ ] #7 Stop-words-only input returns FindSongTooVague response
- [ ] #8 Artist not found returns FindSongArtistNotFound with retry
- [ ] #9 FindSongIntent registered in IntentNames.cs and SlotMappings.cs
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented FindSongIntentHandler with 4-state state machine (null, AwaitingArtist, AwaitingKeywords, Disambiguating). Session state via JSON-serialized FindSongSessionData DTO. Artist-scoped search uses ArtistIds + NameContains DB filter, global search uses NameContains + KeywordMatcher post-filter. Disambiguation resolves picks by number, ordinal words (4 languages), and partial title match. Simplify pass fixed FallbackIntent interception bug, added NameContains to artist query for DB-level filtering, and replaced fake scores with real KeywordMatcher scores. 35 unit tests. FindSongIntent constant added to IntentNames.cs.
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
- [ ] #8 Locale response strings added to all 12 locales
- [ ] #9 Session attributes use FindSongSessionData DTO (no ValueTuples)
- [ ] #10 ShouldEndSession rules match spec table for all states
<!-- DOD:END -->

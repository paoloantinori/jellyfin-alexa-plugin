# FindSongIntent: Conversational Song Search by Partial Title Keywords

**Date:** 2026-06-03
**Status:** Draft
**Depends on:** None
**Related backlog:** DRAFT-1 (N-gram index), DRAFT-2 (Multi-tier search chain)

## Problem

Users often remember the artist and 2-3 words from a song title but not the full title. Alexa's ASR struggles to transcribe long titles accurately, and Jellyfin's `SearchTerm` API requires a close-to-full match. The current `PlaySongIntent` returns "not found" immediately when the partial keywords don't match, with no recovery path.

## Goal

A dedicated conversational intent (`FindSongIntent`) that helps users find songs when they only remember fragments of the title. The user explicitly opts into this flow, so they expect a multi-turn interaction and tolerate slower, more thorough search.

## User Experience

### Happy Path: Artist, Then Keywords (Two Turns)

```
User:  "Find a song by Police"
Skill: "What words do you remember from the title?"
User:  "breath"
Skill: "I found 'Every Breath You Take' by The Police. Playing it now."
       → [plays song]
```

The single-utterance "find a song by Police with breath" is NOT possible because `titleKeywords` uses `AMAZON.SearchQuery`, which cannot coexist with `musician` (a typed slot) in the same utterance. All artist+keyword searches are inherently two-turn.

### Happy Path: Keywords Only (No Artist)

```
User:  "Find a song called breath and take"
Skill: "I found 2 songs: 'Every Breath You Take' by The Police, and
        'Take My Breath Away' by Berlin. Which one?"
User:  "the first one"
Skill: "Playing 'Every Breath You Take' by The Police."
       → [plays song]
```

### Happy Path: No Info Yet

```
User:  "Help me find a song"
Skill: "What words do you remember from the title?"
User:  "breath and take"
       → [continues as keywords-only path above]
```

### Too Many Matches (Global Search Narrows with Artist)

```
User:  "Find a song called love"
Skill: "I found many songs with those words. Can you tell me the artist?"
User:  "Police"
Skill: "I found 2 songs: 'Every Breath You Take' and 'Roxanne'. Which one?"
```

### No Match

```
User:  "Find a song by Police"
Skill: "What words do you remember from the title?"
User:  "xyzzyfoo"
Skill: "I couldn't find a match. Try different words."
User:  "breath"
Skill: "I found 'Every Breath You Take' by The Police. Playing it now."
       → [plays song]
```

### Cancel During Search

```
User:  "Find a song by Police"
Skill: "What words do you remember from the title?"
User:  "stop"
Skill: "Okay, I've stopped searching."
       → [session ends, FindSongState cleared]
```

## Search Algorithm

### Keyword Matching

1. **Tokenize** the user's keyword input: lowercase, split on spaces/punctuation, remove locale-specific stop words.
2. **Tokenize** each candidate song title the same way.
3. **Score** each candidate:
   - `keywordCoverage` = fraction of user keywords found in title tokens (0.0-1.0)
   - `titleCoverage` = fraction of title tokens covered by user keywords (0.0-1.0)
   - `score` = `(0.7 * keywordCoverage + 0.3 * titleCoverage) * 100`
   - Positional bonus: +5 if user keywords match starting from the first title token
4. A song must have `keywordCoverage == 1.0` (all user keywords must appear) to be considered.

### Stop Words

Per-locale short list of common words to strip during tokenization:
- **en-US/en-GB/en-AU/en-CA/en-IN:** the, a, an, of, in, on, at, to, and, or, is, it
- **it-IT:** il, lo, la, i, gli, le, di, del, della, un, una, in, su, per, con, da, e, o, che
- **de-DE:** der, die, das, ein, eine, und, oder, in, an, auf, zu, von, mit
- **fr-FR/fr-CA:** le, la, les, un, une, des, de, du, en, dans, sur, et, ou

Stop words are stored inline in `KeywordMatcher` as a static dictionary keyed by locale prefix. No new locale files needed.

### Edge Case: Stop-Words-Only Input

If tokenization strips all tokens (user said only stop words like "the a"), respond with `FindSongTooVague` and re-prompt.

### Search Scope

The search scope adapts to available information:

| Slots provided | Search scope | Implementation |
|---|---|---|
| Artist + keywords | Artist's songs only | `ArtistIds` filter on `InternalItemsQuery` |
| Keywords only | All songs (DB query) | `NameContains` per keyword via `KeywordMatcher` post-filter |
| Neither | Prompt for more info | No search |

For the global (no-artist) search, the initial implementation uses Jellyfin DB queries: fetch songs with `NameContains` for the first keyword, then `KeywordMatcher` post-filters to enforce all-keywords-must-match. If this proves too slow for large libraries, upgrade to an in-memory song title index (backlog task DRAFT-1).

## Intent Design

**Intent name:** `FindSongIntent`

**Slots:**
- `musician` — existing custom artist slot (reuses existing slot type, same as PlaySongIntent)
- `titleKeywords` — `AMAZON.SearchQuery` slot for free-form multi-word input

### AMAZON.SearchQuery Coexistence Constraint

`AMAZON.SearchQuery` cannot coexist with other slot types in the same utterance (Alexa platform constraint, enforced by `validate_interaction_models.py`). This means:

- Utterances with `{musician}` cannot also have `{titleKeywords}`
- Utterances with `{titleKeywords}` cannot also have `{musician}`
- Both slots CAN be declared on the same intent, but they must appear in SEPARATE utterances

This makes artist+keywords a **two-turn flow** by design. The user first provides the artist (via a `{musician}` utterance), then the handler prompts for keywords and captures them in the follow-up turn.

### Utterances

**en-US:**

Musician-only utterances (no SearchQuery):
- find a song
- find a song by {musician}
- help me find a song
- help me find a song by {musician}
- search for a song
- search for a song by {musician}
- I'm looking for a song
- I'm looking for a song by {musician}
- I need to find a song

Keywords-only utterances (SearchQuery, no musician):
- find a song called {titleKeywords}
- search for a song called {titleKeywords}
- find a song about {titleKeywords}
- I'm looking for a song called {titleKeywords}

Disambiguating concrete samples (prevent NLU competition with SearchMediaIntent and PlayArtistSongsIntent):
- find me a song
- help me search for a song
- I want to find a song

**it-IT:**

Musician-only:
- cerca una canzone
- cerca una canzone di {musician}
- aiutami a trovare una canzone
- aiutami a trovare una canzone di {musician}
- sto cercando una canzone
- sto cercando una canzone di {musician}
- voglio trovare una canzone

Keywords-only:
- trova una canzone chiamata {titleKeywords}
- cerca una canzone chiamata {titleKeywords}
- cerca una canzone che parla di {titleKeywords}

Similar patterns for all locales: de-DE, fr-FR, fr-CA, en-GB, en-AU, en-CA, en-IN, es-ES, es-MX, pt-BR, ja-JP, ar-SA, nl-NL, hi-IN.

### NLU Competition Analysis

**vs. SearchMediaIntent** (which owns "find {query}", "search for {query}"):
- Risk: "search for a song" could route to SearchMediaIntent with `query="a song"`
- Mitigation: Concrete samples with "a song" as fixed text (not slotted) anchor FindSongIntent. The word "song" after "find/search" is a strong discriminant.

**vs. PlayArtistSongsIntent** (which owns "play songs by {musician}"):
- Risk: "find a song by Police" could route to PlayArtistSongsIntent if "find" is treated as synonym of "play"
- Mitigation: "find" is not a PlayArtistSongsIntent sample. Add NLU test fixtures verifying "find a song by X" routes to FindSongIntent, not PlayArtistSongsIntent.

**Action items:**
1. Add NLU test fixtures for FindSongIntent vs SearchMediaIntent boundary cases
2. Add NLU test fixtures for FindSongIntent vs PlayArtistSongsIntent boundary cases
3. Verify routing after model deployment

## Session State

A `FindSongState` enum stored in session attributes to track multi-turn progress:

```
FindSongState:
  - AwaitingArtist     // has keywords, needs artist
  - AwaitingKeywords   // has artist, needs keywords
  - Disambiguating     // presented options, awaiting user pick
```

### Session Data DTOs

All session data uses proper DTOs (not ValueTuples) to avoid the Newtonsoft.Json serialization pitfall:

```csharp
public enum FindSongState
{
    AwaitingArtist,
    AwaitingKeywords,
    Disambiguating
}

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

`KeywordMatcher.Score` may use `List<(BaseItem, double)>` internally but must map to `List<FindSongCandidate>` before storing in session.

## ShouldEndSession Rules

| Situation | ShouldEndSession | Reprompt | Rationale |
|---|---|---|---|
| Multi-turn prompts (AwaitingArtist, AwaitingKeywords) | `false` | "Tell me the artist" / "Tell me the keywords" | Keep session open for follow-up |
| Disambiguation prompt | `false` | "Say the number or the title" | Keep session open for pick |
| No match, re-prompt | `false` | "Try different words" | Allow retry |
| Song playback | `true` | none | AudioPlayer.Play requires true |
| Cancel/Stop during search | `true` | none | End search session |
| Too vague (stop-words-only) | `false` | "Try more specific words" | Allow retry |

## Handler Architecture

### New Files

| File | Purpose |
|---|---|
| `Alexa/Handler/Intent/FindSongIntentHandler.cs` | Multi-turn intent handler |
| `Alexa/Util/KeywordMatcher.cs` | Tokenization, stop words, scoring |
| `Alexa/Locale/ResponseStrings.cs` additions | ~12 new response string keys |
| `Alexa/Locale/<locale>.json` additions | Response strings for all locales |
| `Alexa/IntentNames.cs` addition | `FindSongIntent` constant |
| `Alexa/SlotMappings.cs` addition | `titleKeywords` slot mapping |

### FindSongIntentHandler Flow

```
CanHandle():
  intent == FindSongIntent, OR
  (session.FindSongState is set AND
   (intent == AMAZON.FallbackIntent OR
    intent == FindSongIntent))  // re-invocation with new info

Note: Does NOT intercept AMAZON.YesIntent/AMAZON.NoIntent/AMAZON.StopIntent.
Those are handled by their dedicated handlers. Stop/Cancel naturally ends
the session, clearing FindSongState automatically.

HandleAsync():
  state = session.FindSongState

  // First invocation (no state)
  if state is null:
    musician = slot "musician"
    keywords = slot "titleKeywords"

    if musician AND keywords:
      // Only possible when re-invoked during multi-turn (state was set
      // but session was lost). Treat as fresh search.
      resolve artist → search(musician, keywords)
    elif musician:
      resolve artist → state = AwaitingKeywords → prompt
    elif keywords:
      state = AwaitingArtist → prompt
    else:
      state = AwaitingKeywords → prompt "What words do you remember?"

  // Follow-up in AwaitingArtist state
  // User said the artist name in response to "Who is the artist?"
  // This arrives as FindSongIntent re-invocation with musician slot,
  // or as AMAZON.FallbackIntent with raw transcript text
  if state == AwaitingArtist:
    if intent has musician slot:
      resolve artist → search(artist, keywords)
    else:
      interpret transcript as artist name (reuse artist search)
      if found → search(artist, keywords)
      else → respond FindSongArtistNotFound, keep state

  // Follow-up in AwaitingKeywords state
  // User said keywords in response to "What words do you remember?"
  if state == AwaitingKeywords:
    if intent has titleKeywords slot:
      keywords = titleKeywords
    else:
      // Extract keywords from raw transcript
      keywords = strip carrier phrases from transcript
    search(artist, keywords)

  // Follow-up in Disambiguating state
  // User picked a song from the list
  if state == Disambiguating:
    // Try to match: number ("the first one"), ordinal ("one", "two"),
    // or partial title match against candidates
    match = resolvePick(transcript, candidates)
    if match:
      play match → clear state
    else:
      respond FindSongInvalidPick, keep state

  // Search function
  search(artist?, keywords):
    tokens = KeywordMatcher.Tokenize(keywords)

    if tokens is empty (all stop words):
      respond FindSongTooVague → keep state
      return

    if artist provided:
      songs = GetAllSongsByArtist(artist)
    else:
      songs = SearchSongsByKeyword(tokens[0])  // DB query
      // post-filter with KeywordMatcher

    scored = KeywordMatcher.Score(songs, tokens)
    candidates = scored.filter(keywordCoverage == 1.0)
                       .orderBy(score descending)
                       .take(4)

    if candidates.count == 0:
      respond FindSongNoMatch, keep state for retry
    elif candidates.count == 1 AND score >= 90:
      play song with announcement → clear state
    elif candidates.count >= 1 AND count <= 4:
      state = Disambiguating → list candidates → prompt
    elif candidates.count > 4:
      if artist not provided:
        respond FindSongTooManyNarrow → state = AwaitingArtist
      else:
        take top 4 → disambiguate
```

### KeywordMatcher API

```csharp
public static class KeywordMatcher
{
    // Tokenize: lowercase, split, remove stop words
    public static string[] Tokenize(string text, string locale);

    // Score: returns scored list where all keywords must match
    public static List<(BaseItem Item, double Score)> Score(
        IReadOnlyList<BaseItem> songs,
        string[] keywordTokens,
        string locale);

    // Stop words per locale
    private static readonly Dictionary<string, HashSet<string>> StopWords;
}
```

## Locale Response Strings

New keys to add to `ResponseStrings.cs` and all locale files:

| Key | en-US | it-IT |
|---|---|---|
| `FindSongPromptKeywords` | "What words do you remember from the title?" | "Quali parole ricordi del titolo?" |
| `FindSongPromptArtist` | "Who is the artist?" | "Chi è l'artista?" |
| `FindSongNoMatch` | "I couldn't find a match. Try different words." | "Non ho trovato nulla. Prova con altre parole." |
| `FindSongFoundOne` | "I found {0} by {1}. Playing it now." | "Ho trovato {0} di {1}. La riproduco." |
| `FindSongFoundMultiple` | "I found {0} songs. {1}. Which one?" | "Ho trovato {0} canzoni. {1}. Quale?" |
| `FindSongTooManyNarrow` | "I found many songs with those words. Can you tell me the artist?" | "Ho trovato molte canzoni con quelle parole. Sai dirmi l'artista?" |
| `FindSongPlaying` | "Playing {0} by {1}." | "Riproduco {0} di {1}." |
| `FindSongDisambiguatePick` | "Which one? Say the number or the title." | "Quale? Di' il numero o il titolo." |
| `FindSongArtistNotFound` | "I couldn't find an artist called {0}. Try again." | "Non ho trovato un artista chiamato {0}. Riprova." |
| `FindSongTooVague` | "I need more specific words to search. Try again with a few words from the title." | "Ho bisogno di parole più specifiche. Riprova con alcune parole del titolo." |
| `FindSongInvalidPick` | "I didn't catch that. Please say the number or the title of the song." | "Non ho capito. Di' il numero o il titolo della canzone." |
| `FindSongCancelled` | "Okay, I've stopped searching." | "Ok, ho interrotto la ricerca." |

## Interaction Model Changes

Add `FindSongIntent` to all locale interaction models with the utterance patterns defined above. Add `titleKeywords` slot mapped to `AMAZON.SearchQuery`.

Since it-IT is generated from YAML template, update `Alexa/InteractionModel/templates/it-IT.yaml` and regenerate.

### All Locales

en-US, en-GB, en-AU, en-CA, en-IN, it-IT, de-DE, fr-FR, fr-CA, es-ES, es-MX, pt-BR, ja-JP, ar-SA, nl-NL, hi-IN. Verify against actual model files during implementation.

## Edge Cases

| Case | Behavior |
|---|---|
| User says stop/cancel during multi-turn | Session ends naturally via StopIntent. FindSongState cleared by session expiry. No special handling needed. |
| Stop-words-only input | Respond `FindSongTooVague`, keep state for retry. |
| Invalid pick during disambiguation | Respond `FindSongInvalidPick`, keep state for retry. |
| Session timeout during multi-turn | FindSongState lost with session. Expected behavior, no special handling. |
| User says "no" during prompt | Treat as no-match. `FindSongNoMatch` response, keep state for retry. User can say different words. |
| Artist not found | Respond `FindSongArtistNotFound`, keep state for retry. |

## Testing Plan

### Unit Tests

1. **KeywordMatcher.Tokenize** — stop word removal, punctuation handling, locale-aware
2. **KeywordMatcher.Score** — keyword coverage, title coverage, positional bonus
3. **FindSongIntentHandler** — all state transitions (first invocation, follow-up artist, follow-up keywords, disambiguation pick)
4. **Edge cases** — empty keywords, single-word keywords, stop-words-only input, very long keywords, invalid disambiguation pick

### NLU Tests

1. "find a song by Police" → routes to FindSongIntent (not PlayArtistSongsIntent)
2. "find a song called breath" → routes to FindSongIntent (not SearchMediaIntent)
3. "search for a song" → routes to FindSongIntent (not SearchMediaIntent)
4. "help me find a song by Radiohead" → routes to FindSongIntent

### E2E Tests

1. "find a song by [artist]" → verify prompt for keywords
2. Multi-turn: "find a song by [artist]" → "[keywords]" → verify song found and played
3. "help me find a song" → verify multi-turn flow
4. Keywords-only: "find a song called [keywords]" → verify global search
5. Disambiguation: verify pick-by-number and pick-by-title work

## Future Extensions

- **In-memory song index** (DRAFT-1): Replace DB query for global search with pre-built token index for faster lookups
- **Multi-tier search chain** (DRAFT-2): Add more search tiers beyond keyword subset matching
- **Fuzzy keyword matching**: Allow keywords to fuzzy-match title tokens (not just exact subset), useful for ASR errors in keywords themselves

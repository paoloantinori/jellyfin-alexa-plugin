# Research: Alexa Slot Type Strategy for Mixed-Language Music Recognition

**Date**: 2026-05-07
**Scope**: Improving English song/artist recognition when users speak Italian (it-IT locale)
**Confidence**: HIGH (three independent agents, SMAPI spec verification, Amazon docs cross-referenced)

---

## Executive Summary

The current interaction model architecture is already well-designed. The core issue is NOT a slot type misconfiguration — it's that **Amazon's it-IT acoustic model has limited coverage of English proper nouns**. For well-known artists (Pink Floyd, Ed Sheeran, Coldplay), recognition works. For less-known ones, it fails.

Three viable improvement paths are ranked below. The recommended approach combines **Options A + C** for maximum impact with moderate effort.

---

## Current State Analysis

### What We Have
- `PlaySongIntent`: `{song: AMAZON.MusicRecording}` + `{musician: AMAZON.Musician}` — structured "song by artist"
- `PlayArtistSongsIntent`: `{musician: AMAZON.Musician}` — "play songs by artist"
- `PlayVideoIntent` / `SearchMediaIntent`: `{title/query: AMAZON.SearchQuery}` — unstructured fallback
- Italian locale already has 10 custom slot types (vs 4 in German/French/Spanish)
- Italian model uses custom `AlbumName` type (8 values) instead of `AMAZON.MusicRecording` for albums

### Key Constraints (Hard Limitations)
1. **AMAZON.SearchQuery cannot share an utterance with another slot** — per-utterance, not per-intent. You CAN declare both slot types in the intent, but each sample utterance can only reference one.
2. **Music Skill API** (catalog upload, used by Spotify/Apple Music) is **US-only and unavailable for custom skills** — not an option.
3. **Slot name consistency**: Same slot name must use same slot type across all intents in a locale.
4. **AMAZON.MusicRecording is NOT an enumeration** — it returns values outside its training data. The listed examples weight recognition but don't constrain it.

### What Amazon's it-IT Models Already Know
- `AMAZON.Musician` (it-IT): Jovanotti, Vasco Rossi, Andrea Bocelli, Mina, Ed Sheeran, Coldplay, Imagine Dragons, Bon Jovi
- `AMAZON.MusicRecording` (it-IT): Luna caprese, Il cielo in una stanza, Thinking Out Loud, Hallelujah, What A Wonderful World
- `AMAZON.MusicGroup` (it-IT): Pink Floyd, Tiromancino, Negramaro, Elio e le Storie Tese, Stadio, 99 Posse
- `AMAZON.MusicAlbum` (it-IT): Fatti sentire, Plume, Evolve, Thriller, Simili

**Implication**: Well-known English artists/songs are already in the Italian model. The gap is with **less popular or niche content**.

---

## Ranked Options

### Option A: Improve Sample Utterances + Self-Managed Slot Elicitation
**Effort**: LOW | **Impact**: MEDIUM | **Risk**: LOW

**What**:
1. Add more Italian carrier phrases wrapping English proper nouns to `PlaySongIntent`:
   - `"suona {song} di {musician}"`
   - `"riproduci {song}"`
   - `"metti {song}"`
   - `"fai partire {song} di {musician}"`
   - `"ascolta {song}"`
2. Remove the broken `Dialog.Delegate` calls from handlers (they fail because it-IT has no dialog model). Replace with self-managed slot elicitation using `ElicitSlot` directives:
   ```csharp
   // Instead of DelegateToDialog(), use:
   if (string.IsNullOrEmpty(songSlot))
   {
       return ResponseBuilder.Ask("Quale canzone vuoi ascoltare?", null);
   }
   ```
3. Add more concrete (non-slotted) utterances to disambiguate NLU competition.

**Why it helps**: More sample utterances with Italian carrier phrases give Alexa's NLU better patterns to match against. The built-in types already support cross-lingual proper nouns — the bottleneck is intent/slot matching confidence.

**Pros**: No interaction model restructuring, fixes Dialog.Delegate crash, quick to implement
**Cons**: Doesn't fundamentally solve the "unknown artist" problem

---

### Option B: Dynamic Entity Resolution (Per-Session Library Upload)
**Effort**: MEDIUM-HIGH | **Impact**: HIGH | **Risk**: MEDIUM

**What**:
1. When a user starts a session, query their Jellyfin library for top artists/albums
2. Send `Dialog.UpdateDynamicEntities` directive with these values as session-scoped slot overrides
3. The NLU engine now has the user's actual library content in its vocabulary for that session

**How it works**:
```json
{
  "type": "Dialog.UpdateDynamicEntities",
  "updateBehavior": "REPLACE",
  "slotTypes": [
    {
      "name": "musician",
      "values": [
        {"id": "artist_123", "name": {"value": "Queen", "synonyms": ["kuin", "i queen"]}},
        {"id": "artist_456", "name": {"value": "Led Zeppelin", "synonyms": ["led zeplin"]}}
      ]
    }
  ]
}
```

**Why it helps**: The NLU engine gets a targeted vocabulary of the user's actual library. Italian-accented pronunciation of "Queen" matches better when "Queen" is explicitly in the entity list with phonetic synonyms.

**Pros**: Per-user personalization, runtime-only (no model rebuild), supports synonyms for phonetic variants
**Cons**: Session-scoped only (lost on session end), requires initial API call on session start, SMAPI complexity, limited to ~100 dynamic values per session

---

### Option C: Catalog-Based Custom Slot Types (Static Library Upload)
**Effort**: HIGH | **Impact**: HIGH | **Risk**: MEDIUM

**What**:
1. Use SMAPI catalog API to upload the user's top artists/albums as a persistent catalog
2. Create slot types referencing the catalog via `CatalogValueSupplier`:
   ```json
   {
     "valueSupplier": {
       "type": "CatalogValueSupplier",
       "valueCatalog": {"catalogId": "jellyfin-artists-catalog", "version": "1"}
     }
   }
   ```
3. Use `ReferencedResourceVersionUpdate` SMAPI jobs to auto-sync when catalog updates
4. Per-user catalogs (one per Jellyfin user) mapped to skill stage

**Why it helps**: Persistent (survives sessions), supports up to 50,000 values per slot type, provides entity resolution with canonical IDs that map directly to Jellyfin entity IDs.

**Pros**: Persistent across sessions, large value capacity, entity resolution with exact IDs, auto-sync via SMAPI jobs
**Cons**: Per-user management complexity, SMAPI catalog API complexity, model rebuild needed on catalog update, 1.5MB model size limit per locale

---

### Option D: Hybrid AMAZON.SearchQuery with Server-Side NLP
**Effort**: MEDIUM | **Impact**: MEDIUM | **Risk**: LOW

**What**:
1. Create a dedicated `PlayMusicFreeformIntent` using only `AMAZON.SearchQuery`
2. Add Italian carrier phrases: `"suona il brano {query}"`, `"metti {query}"`, `"riproduci {query}"`
3. Server-side: parse the raw query string to extract artist/song using separator detection:
   ```csharp
   // Split on "di", "by", "von", "dei"
   var parts = Regex.Split(query, @"\s+(?:di|dei|by|von)\s+", RegexOptions.IgnoreCase);
   ```
4. Fuzzy-match each part against Jellyfin's artist/track search

**Why it helps**: `AMAZON.SearchQuery` returns raw ASR output without entity resolution constraints. If the acoustic model hears "bohemian rhapsody" (even imperfectly), you get the raw text to fuzzy-match.

**Pros**: No entity resolution dependency, works for any title, fuzzy matching compensates for ASR errors
**Cons**: Loses built-in disambiguation, requires separator parsing, no entity resolution confidence scores, NLU may confuse with other intents

---

## Recommendation: A + C (Phased)

### Phase 1 (Immediate) — Option A
1. Remove broken `Dialog.Delegate` calls, replace with self-managed `ElicitSlot` or `Ask` responses
2. Add 15-20 more Italian carrier phrase variations for `PlaySongIntent`
3. Add concrete (non-slotted) utterances for disambiguation

### Phase 2 (Next Release) — Option C (Simplified)
1. Upload user's top 200 artists as a static catalog via SMAPI
2. Create `JellyfinArtist` custom slot type referencing the catalog
3. Add phonetic synonyms for English names in Italian pronunciation
4. Use `ReferencedResourceVersionUpdate` job for periodic refresh

### Optional Enhancement — Option B
If per-session personalization is desired, add `Dialog.UpdateDynamicEntities` on session start with the user's most-played items.

---

## Sources

- Amazon slot type reference: https://developer.amazon.com/en-US/docs/alexa/custom-skills/slot-type-reference.html
- Custom slot types: https://developer.amazon.com/en-US/docs/alexa/custom-skills/create-and-edit-custom-slot-types.html
- Dynamic entities: https://developer.amazon.com/en-US/docs/alexa/custom-skills/use-dynamic-entities-for-customized-interactions.html
- SMAPI spec v1.24.0: https://unpkg.com/ask-smapi-model@1.24.0/spec.json
- SMAPI catalog APIs: https://developer.amazon.com/en-US/docs/alexa/music/upload-music-or-radio-catalogs.html
- Built-in intent library (Music): https://developer.amazon.com/en-US/docs/alexa/custom-skills/built-in-intent-library/musicrecording-intents.html
- ASK SDK Node.js (Context7): Context7 query for entity resolution and dialog directives

# Research: Mitigating Alexa NLU Misspelling and Intent Competition Issues

**Date**: 2026-05-17
**Context**: JF-164 in-memory artist index works perfectly for fuzzy matching, but two utterance classes fail at the NLU level before reaching our handler.

## Problem Statement

### Issue 1: Heavy misspellings don't resolve to any intent
- **Example**: "suona i pink floid" → Alexa NLU cannot map to any intent
- **Cause**: `AMAZON.Musician` slot type uses Amazon's trained model, not our artist list. Heavy deviations from known entity names fail NLU routing entirely.
- **Impact**: Our fuzzy matcher never gets a chance — the utterance dies at NLU.

### Issue 2: Intent competition (keyword collision)
- **Example**: "suona i radiohead" → routes to `PlayRadioIntent` instead of `PlayArtistSongsIntent`
- **Cause**: "radio" is a keyword in `PlayRadioIntent` utterances ("suona la radio", "suona brani simili"). Alexa's NLU matches the prefix before considering the artist intent.
- **Impact**: Wrong handler executes, user gets radio mode instead of artist playback.

## Findings

### F1: Dynamic entities already exist but only cover recent items

The project has a `DynamicEntityBuilder` + `DynamicEntitiesInterceptor` that injects `Dialog.UpdateDynamicEntities` directives into responses. However:

- **Scope**: Only injects artists from the user's **recently added** items (55 per type, 90 total budget)
- **Timing**: Only fires on **new sessions** (`LaunchRequest` or `session.New`)
- **Limit**: 100 total values+synonyms per directive (Alexa hard limit)
- **Slot type**: Injects into `AMAZON.Musician` — the same slot type used by `PlayArtistSongsIntent`

This means dynamic entities **already solve** the misspelling problem for recently-added artists, but not for the user's full library.

### F2: `fallbackIntentSensitivity` is unset

The `model_it-IT.json` has an empty `modelConfiguration: {}`. Setting `fallbackIntentSensitivity` to `LOW` would make the NLU more aggressive about routing utterances to custom intents rather than falling back to `AMAZON.FallbackIntent`. This could help with some borderline cases but won't fix utterances that completely fail NLU routing.

**Availability**: `fallbackIntentSensitivity` is documented as available for `en-XX` and `de-DE` locales. It-IT support is unclear.

### F3: Alexa's NLU processes intent routing before slot filling

Alexa's NLU pipeline is:
1. ASR converts speech → text
2. NLU matches text → intent (considering ALL intents' utterances)
3. NLU fills slots for the matched intent

The NLU uses a **statistical model** trained on sample utterances + slot values, not exact matching. This means:
- Carrier phrases ("suona i", "metti una canzone dei") strongly influence intent selection
- Slot values act as training data, not an enumeration — Amazon's model generalizes beyond listed values
- When two intents share similar carrier phrases, the NLU picks based on overall statistical match

### F4: Dynamic entities take priority over static slot types

From Amazon's docs: "Dynamic values are given priority over static values." When `Dialog.UpdateDynamicEntities` is sent with artist names, those names become the **preferred** values for the `AMAZON.Musician` slot. This is the most powerful lever available.

### F5: Catalog-based slot types provide persistent entity data

The project has `CatalogSlotTypes` mapping `JellyfinArtist` as a catalog-backed slot type. If the interaction model used `JellyfinArtist` instead of `AMAZON.Musician`, the full catalog of artists would be available to NLU at all times (not just session-scoped dynamic entities).

## Mitigation Strategies

### Strategy A: Expand dynamic entities to include ALL library artists (Recommended)

**What**: On session start, inject the full artist list (from the new `IArtistIndex`) into `Dialog.UpdateDynamicEntities`, not just recent items.

**How**:
- Modify `DynamicEntityBuilder` to accept an `IArtistIndex` parameter
- Build dynamic entity values from the full in-memory index instead of recent-item queries
- Use a round-robin or frequency-weighted selection to stay within the 100-value limit
- Continue injecting on new sessions via `DynamicEntitiesInterceptor`

**Pros**: 
- Solves Issue 1 (misspellings) for artists in the dynamic entity set — Amazon's NLU biases toward dynamic values
- No interaction model changes required
- Works with existing infrastructure

**Cons**: 
- 100-value limit means only a subset of artists can be injected (a library with 2000 artists covers ~5%)
- Only session-scoped — first utterance of a new session has no dynamic entities loaded yet
- Doesn't help with Issue 2 (intent competition with "radio")

**Confidence**: High — this is exactly what dynamic entities are designed for.

### Strategy B: Switch musician slot from `AMAZON.Musician` to catalog-backed `JellyfinArtist`

**What**: Replace the built-in `AMAZON.Musician` slot type in the interaction model with the custom `JellyfinArtist` catalog type that's already defined.

**How**:
- Change `PlayArtistSongsIntent.slots.musician.type` from `AMAZON.Musician` to `JellyfinArtist` in all 17 locale models
- Ensure `CatalogSyncTask` populates the `JellyfinArtist` catalog with all library artists
- The catalog provides persistent entity data that Alexa's NLU uses for matching

**Pros**:
- Full artist catalog available to NLU at all times (no session scoping)
- Better ASR accuracy for artist names (catalog values include phonetic matching)
- Solves Issue 1 (misspellings) for all catalog artists, not just recent 100

**Cons**:
- Requires catalog sync to be working correctly across all 17 locales
- Catalog changes require SMAPI deployment and rebuild (~15-30 min)
- If catalog is empty (first install), `AMAZON.Musician` fallback is better
- The `JellyfinArtist` catalog may not exist for all locales yet

**Confidence**: Medium — catalog-backed slots are documented but the project's catalog infrastructure needs verification.

### Strategy C: Disambiguate radio vs artist with carrier phrase differentiation

**What**: Add more specific carrier phrases to `PlayArtistSongsIntent` that don't collide with `PlayRadioIntent`.

**How**:
- Add concrete (non-slotted) utterances like "metti musica di pink floyd" that disambiguate from radio
- Add utterances with explicit artist markers: "suona l'artista {musician}", "riproduci l'artista {musician}"
- Remove or reduce ambiguous carrier phrases shared with radio intent

**Pros**:
- Directly addresses Issue 2 (intent competition)
- Simple interaction model change
- Documented best practice for NLU competition

**Cons**:
- Only helps if users say the new phrases
- Doesn't fix the underlying "radio" keyword collision
- May require adding many locale-specific variants

**Confidence**: High for partial mitigation — this is the standard Alexa approach for intent competition.

### Strategy D: Set `fallbackIntentSensitivity` to LOW

**What**: Add `"fallbackIntentSensitivity": {"level": "LOW"}` to all locale interaction models.

**How**: Add to `modelConfiguration` in each locale model JSON.

**Pros**:
- Simple config change
- Routes more borderline utterances to custom intents instead of FallbackIntent
- May help some misspelling cases

**Cons**:
- May cause false positives (out-of-domain utterances routed to wrong intents)
- Locale support uncertain (documented for en-XX, de-DE only)
- Doesn't fix utterances that NLU can't route to ANY intent (like "pink floid")

**Confidence**: Low impact — helps at the margins but doesn't address root causes.

### Strategy E: Hybrid — Combine A + C (best near-term approach)

**What**: Expand dynamic entities (A) to cover more artists AND add disambiguating carrier phrases (C).

**Implementation**:
1. Modify `DynamicEntityBuilder` to use `IArtistIndex` for a broader artist selection
2. Prioritize recently-played + favorited + popular artists in the 100-value budget
3. Add locale-specific carrier phrases with "artista" marker: "suona l'artista {musician}"
4. Set `fallbackIntentSensitivity: LOW` as a low-cost additional improvement

**Expected impact**:
- Issue 1 (misspelling): Significantly reduced for top-100 artists via dynamic entities
- Issue 2 (intent competition): Mitigated by "artista" carrier phrases
- First-utterance gap: Not solved — the very first request in a session still has no dynamic entities

## What We Cannot Fix

1. **First utterance of a session**: Dynamic entities only take effect after the skill returns a response containing `Dialog.UpdateDynamicEntities`. The very first utterance has no dynamic entities loaded. This is a fundamental Alexa platform limitation.

2. **ASR errors that produce non-artist words**: If ASR mishears "Pink Floyd" as something completely different (e.g., "Pink Float"), no NLU model adjustment will help.

3. **Global Amazon skill competition**: On Echo devices, Amazon's built-in Music skill competes for music-related utterances. This is outside developer control.

## Sources

- [Alexa Dynamic Entities](https://developer.amazon.com/blogs/alexa/post/db4c0ed5-5a05-4037-a3a7-3fe5c29dcb65/use-dynamic-entities-to-create-personalized-voice-experiences) — runtime slot value injection, 100-value limit
- [FallbackIntent Sensitivity](https://developer.amazon.com/en-US/docs/alexa/interaction-model-design/tips-for-using-built-in-intents-for-your-skill.html) — LOW/MEDIUM/HIGH tuning for intent routing
- [Reference-Based Catalog Management](https://developer.amazon.com/en-US/blogs/alexa/alexa-skills-kit/2020/09/automatic-updates-for-reference-based-catalogs) — persistent catalog-backed slot types
- [Utterance Conflict Detection](https://developer.amazon.com/blogs/alexa/post/a5b37f34-83c8-4274-b576-073a21dfdb7a/build-test-and-tune-your-skills-with-three-new-tools1) — NLU conflict resolution tools
- [Alexa Meaning Representation Language](https://www.amazon.science/blog/new-way-to-annotate-training-data-should-enable-more-sophisticated-alexa-interactions) — how Alexa processes domain/intent/slot

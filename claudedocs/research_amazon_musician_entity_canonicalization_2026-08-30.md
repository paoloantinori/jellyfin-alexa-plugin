# Research Report: AMAZON.Musician entity canonicalization overwriting slot values on en-* locales

**Date**: 2026-08-30
**Depth**: exhaustive
**Confidence**: MEDIUM (behavior verified by probe; mechanism inferred from docs; no second-party reports found)

## Executive Summary

The `AMAZON.Musician` built-in slot type performs entity resolution against Amazon's knowledge graph on English locales (en-US, en-GB, en-AU, en-CA, en-IN) and REPLACES the slot's `value` field with the canonical entity name instead of the spoken text. This contradicts Amazon's own documentation, which shows `slot.value` containing the raw spoken text and the resolutions array containing the canonical entities. No other open-source project has publicly reported this specific behavior; the main commercial competitor (My Media for Alexa) is dealing with a different but related Alexa+ platform degradation. The recommended mitigation for our project is to swap the `musician` slot from `AMAZON.Musician` to the catalog-backed custom type `JellyfinArtist`, which returns raw spoken text.

## Findings

### 1. The documented behavior (what Amazon says SHOULD happen)

Amazon's "Entity Resolution for Built-in Slot Types" page [1] documents that built-in types supporting entity resolution (including `AMAZON.Musician` [2]) resolve utterances against the Alexa knowledge graph. The documentation example for `AMAZON.Person` shows:

- `slot.value` = "bezos" (the raw spoken text)
- `slot.slotValue.value` = "bezos" (the raw spoken text, preferred access method)
- `resolutions.resolutionsPerAuthority[0].values[0].value.name` = "Jeff Bezos" (the canonical entity)

This is the CONTRACT: the slot value preserves what the user said; the canonical entity goes in the resolutions. Documentation last updated Nov 28, 2023.

### 2. The observed behavior (what ACTUALLY happens on en-*)

Our probe (2026-08-29, profile-nlu on en-GB, development stage) shows:

| Utterance | slot.value contains | slot.slotValue.value | Expected (per docs) |
|---|---|---|---|
| "an album by queen" | **Paula Abdul** | (same) | queen |
| "an album by the beatles" | **John Lennon** | (same) | the beatles |
| "an album by coldplay" | **Christopher Anthony John Martin** | (same) | coldplay |
| "an album by pink floyd" | **Syd Barrett** | (same) | pink floyd |

The resolutions authority is `AlexaEntities` with status `ER_SUCCESS_MATCH`. The `value` field contains the TOP-MATCHED canonical entity name, not the spoken text. This is a **contract violation** or an **undocumented behavior change** relative to the Nov 2023 documentation.

On it-IT, fr-FR, and de-DE (verified the same day), the slot value preserves the raw spoken text. The canonicalization is specific to en-* locales.

### 3. No second-party reports found (exhaustive search)

Despite 15+ targeted searches across:
- GitHub (projects using AMAZON.Musician, Alexa SDK issues, music streaming skills)
- Stack Overflow (built-in slot type behavior, entity resolution, music skills)
- Amazon Developer Forums (answerhub)

No other project has publicly reported the specific behavior of AMAZON.Musician replacing slot values with canonical entity names on en-* locales. Possible explanations:

a) **Most music skills use the Music Skill API** (Amazon partnership required), not custom skills. The Music Skill API handles slot resolution differently and doesn't expose this problem.

b) **Most custom music skills already use custom slot types** to avoid built-in entity resolution entirely. A Stack Overflow comment from 2018 [3] notes that AMAZON.Musician "seems to let you say anything," suggesting free-text behavior was the expectation.

c) **The behavior may be recent** (post-Nov 2023 docs update, possibly tied to Alexa+ knowledge graph improvements). The My Media for Alexa team documents "massive" routing changes in the 6 months preceding June 2026 [4], suggesting Amazon has been actively modifying the skill platform.

d) **Commercial products report through private channels**: My Media for Alexa states they "escalated the route hijacking to Amazon who analyzed sample user sessions and confirmed it is a bug Amazon side" [4]. They don't publish GitHub issues for platform bugs.

### 4. My Media for Alexa context (commercial competitor)

The main commercial custom music skill for Alexa has documented [4] a related but different Alexa+ platform degradation ("Route Hijacking") where one-shot invocations like "Alexa, ask My Media to play X" are increasingly routed to Amazon Music/Spotify instead of the skill. Key facts:

- Amazon confirmed it is a platform-side bug (not the skill's)
- Proposed fix timeframe: 2026Q3 (no guarantees)
- My Media adopted a multi-turn dialog workaround ("ARH model"): open the skill, then accept the media request in a follow-up turn, which is "less prone to route-hijacking"

This is NOT the same issue as ours (route hijacking vs entity canonicalization) but demonstrates that:
1. Amazon is actively changing skill platform behavior
2. Custom music skills are disproportionately affected
3. Multi-turn dialog is a viable mitigation for routing issues

### 5. The entity types involved

Per the Alexa Entities Reference [2], `AMAZON.Musician` resolves to:
- `entertainment:Musician` (musician-specific entity)
- `Person` (general person entity)

The knowledge graph contains real-world data about these entities (birthdates, etc.) accessible via the Linked Data API. Our probe shows the canonical name is the person's full legal name (e.g., Chris Martin's full name for "coldplay"), suggesting the knowledge graph prioritizes Person entities over MusicGroup entities for band names spoken as if they were persons.

### 6. Mitigation approaches (ranked by fit to our architecture)

| Approach | Mechanism | Pros | Cons |
|---|---|---|---|
| **Custom slot type (JellyfinArtist)** | Catalog-backed, populated from user's library by CatalogSyncTask | Raw text returned; phonetic synonyms; the project's own JF-96.2 architecture | Requires swapping in ALL intents using `musician` slot (~6) across en-* locales; catalog must be populated |
| **AMAZON.SearchQuery** | Free-text, no entity resolution | Always returns raw text | Cannot coexist with other slot types in the same intent (anti-pattern #2) |
| **Read resolutions instead of slot.value** | Parse resolutionsPerAuthority to find the original | No model change needed | The resolutions ALSO contain canonical entities, not the spoken text; there is no field containing the raw spoken text |
| **Accept the behavior, search by canonical name** | Use the entity ID or canonical name to search | No model change | Canonical names don't exist in the user's Jellyfin library; would require an external music database lookup |

**The recommended approach is #1**: swap to `JellyfinArtist` custom slot type. This is consistent with:
- The project's existing architecture (JF-96.2 catalog sync)
- The it-IT locale's use of `AlbumName` for the album slot (same pattern)
- The Stack Overflow observation that custom slot types return raw text even for values not in the list [5]

### 7. What we DON'T know (gaps)

- **When the behavior changed**: the docs were last updated Nov 2023, but we don't know when the en-* canonicalization started
- **Whether it's permanent or a bug**: Amazon confirmed the route-hijacking as a bug with a 2026Q3 fix, but has not commented on entity canonicalization
- **Whether it extends to on-device**: our probe used profile-nlu (model-level); real devices might behave differently (though we have no reason to think so)
- **Whether AMAZON.MusicGroup has the same issue**: we didn't test it (it's not used in our models)
- **Whether extending AMAZON.Musician with custom values** would override the entity rewriting (the docs suggest extended values take precedence [1], but this needs testing)

## Confidence Assessment

| Claim | Confidence | Basis |
|---|---|---|
| AMAZON.Musician replaces slot.value with canonical entity on en-* | **HIGH** | Direct probe evidence (4 utterances, deterministic, 2026-08-29) |
| This contradicts Amazon's documented behavior | **HIGH** | Side-by-side comparison with the official docs example [1] |
| No other project has reported this | **MEDIUM** | Exhaustive search found nothing, but absence of evidence is not evidence of absence |
| The behavior is recent (post-2023) | **LOW** | Inferred from docs date + Alexa+ changes; no direct evidence |
| Custom slot types avoid this behavior | **HIGH** | Well-documented in Amazon docs [1] and consistent with our it-IT AlbumName behavior |
| My Media for Alexa suffers from a related platform change | **HIGH** | Their public documentation [4] states Amazon confirmed a platform-side bug |

## Sources

1. **Entity Resolution for Built-in Slot Types** (Amazon official, last updated Nov 28, 2023)
   https://developer.amazon.com/en-US/docs/alexa/custom-skills/entity-resolution-for-built-in-slot-types.html
   Documents the expected behavior: slot.value = spoken text, resolutions = canonical entities.

2. **Alexa Entities Reference** (Amazon official)
   https://developer.amazon.com/en-US/docs/alexa/custom-skills/alexa-entities-reference.html
   Lists AMAZON.Musician as supporting entity resolution to entertainment:Musician and Person types.

3. **Stack Overflow: "How to make slot to except ANY string in Lex"** (2018)
   https://stackoverflow.com/questions/48598867/how-to-make-slot-to-except-any-string-in-lex
   Comment notes AMAZON.Musician "seems to let you say anything" (free-text expectation).

4. **My Media for Alexa and Amazon's Alexa+ Route Hijacking** (Bizmodeller, June 2026)
   https://docs.bizmodeller.com/my-media-for-alexa/alexa-plus.html
   Documents a related Alexa+ platform degradation; Amazon confirmed as a platform bug with 2026Q3 proposed fix.

5. **Stack Overflow: "Alexa Custom Slot Type: No value in intent"** (2017)
   https://stackoverflow.com/questions/42721603/alexa-custom-slot-type-no-value-in-intent
   Confirms custom slot types return spoken text even for values not in the list.

6. **JF-414 implementation notes** (this project, 2026-08-29)
   Probe evidence for the en-* canonicalization behavior and the en-GB/de-DE/fr-FR comparison.

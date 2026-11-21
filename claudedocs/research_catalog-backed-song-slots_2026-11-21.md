# Research Report: Catalog-backed song slots for Alexa custom skills (SMAPI limits, hybrid approaches, high-volume strategies)

**Date**: 2026-11-21
**Depth**: exhaustive
**Confidence**: HIGH (official Amazon documentation for slot type behavior and limits; Stack Overflow corroborated by the official docs; one finding from our own live evidence)

## Executive Summary

A catalog-backed song slot type is feasible and the infrastructure already exists in the plugin, but the design must choose between two mechanisms with opposite behaviors: static custom slot type values (training-data "guidelines" that do NOT block unknown titles) versus valueSupplier.valueCatalog (hard entity-resolution data that DOES gate intent selection for unmatched values, per our own JF-684 live evidence). The recommended design is a DUAL-SLOT hybrid: keep PlaySongIntent with AMAZON.MusicRecording (free-text, captures anything) and add a second intent with a catalog-backed SongTitle slot for library-anchored routing, so known songs get the strong anchor and unknown songs keep the free-text fallback.

## Findings

### 1. Slot type value limits

**Static custom slot types**: A skill can have a total of **50,000 custom slot values, totaled across ALL custom slot types** in the interaction model [1]. For our plugin, JellyfinArtist (hundreds) and AlbumName (hundreds) consume a small fraction of this budget; a 10,000-song library fits comfortably.

**Catalog-backed types (valueSupplier.valueCatalog)**: No documented per-catalog value limit in the SMAPI Interaction Model Catalog API Reference [2]. The catalog is uploaded as a version via a URL and fetched by Amazon; the limit is practical (JSON payload size, ingestion time), not a documented hard cap.

**Dynamic entities (Dialog.UpdateDynamicEntities)**: Limited to **100 entities** per update, auto-clear after 30 minutes [3]. Unsuitable for a full music library.

### 2. The critical behavioral distinction: static values vs catalog-backed types

This is the most important finding and the one that shapes the entire design.

**Static custom slot type values are GUIDELINES, not constraints.** The official documentation states: "A custom slot type is not the equivalent of an enumeration. Values outside the list are still returned if recognized by the spoken language understanding system. Although input to the slot type is weighted towards the values in the list, it is not constrained to just the items on the list" [4]. Multiple Stack Overflow answers corroborate this: "the values you provide are NOT a strict enum or array that limit what the user can say" [5] and "sample values you provide for a Custom Slot Type are just a 'guideline' to Alexa" [6].

**Catalog-backed types (valueSupplier.valueCatalog) DO gate intent selection.** Our own JF-684 live A/B evidence (2026-09-30): "when a spoken value matches NO catalog value or synonym, a catalog-backed slot type (valueSupplier.valueCatalog) makes the NLU select NO intent at all: the request never reaches the skill, so the handler-side fuzzy chain is voice-unreachable for bare/partial names" [7]. This was verified against the live v1188 catalog with real utterances.

**Implication**: Switching PlaySongIntent's {song} slot from AMAZON.MusicRecording to a catalog-backed type would FIX routing for known songs (the catalog anchors them) but BREAK routing for any song not yet synced or not in the catalog (the NLU would refuse to select the intent). This is the exact tradeoff the design must navigate.

### 3. Can a slot use both catalog and free-text types?

No. A slot has exactly one type. You cannot hybridize AMAZON.MusicRecording with a custom catalog type within the same slot.

However, you CAN define two different intents with different slot types and different sample utterances, providing the hybrid at the MODEL level rather than the SLOT level. This is the dual-intent approach described in the Design Recommendation below.

### 4. How music skills handle large catalogs

The Music Skill API (used by Spotify, Pandora, etc.) uses a completely different catalog mechanism from custom skills: the skill provides structured catalog data (entities with IDs, names, popularity, language) to Amazon, and Amazon's NLU handles matching natively [8]. A Medium article describing a personal music streaming skill uses this Music Skill API catalog upload mechanism, which is NOT available to custom skills [9].

For custom skills, the only three mechanisms are: static values in the model, valueSupplier.valueCatalog, and Dialog.UpdateDynamicEntities. There is no fourth option.

### 5. Sync-time and payload costs for 10k+ values

**Catalog-backed**: The catalog version is served from a URL (our plugin serves it from CatalogController). A 10k-song catalog with phonetic synonyms would be a JSON payload of roughly 2-5 MB. The catalog upload is a versioned operation (SMAPI fetches it once per version), not a model rebuild. This is manageable.

**Static values**: The values go into the interaction model JSON itself. A 10k-value type with synonyms would add several MB to the model JSON, which would need to be rebuilt and redeployed every time the library changes. This is heavier than the catalog approach but preserves the "guideline" behavior.

### 6. Progressive and lazy loading strategies

There is no built-in progressive loading for catalog types. The catalog API supports versioning (you create a new version at any time), but the entire catalog is fetched per version. For very large libraries, a practical subset strategy: sync only the most-played or most-recently-added songs (e.g., top 1000-5000) to keep the catalog manageable, while the free-text intent covers the rest.

## Design recommendation

**The dual-intent hybrid** (strongest recommendation, MEDIUM confidence because it requires live verification):

1. Keep PlaySongIntent exactly as it is: {song} of type AMAZON.MusicRecording, free-text, captures any title. This preserves the current working behavior for all songs, famous and unknown.

2. Add a new intent (e.g., PlayLibrarySongIntent) with a {librarySong} slot of a catalog-backed SongTitle type, populated from the user's library during catalog sync. The samples use a distinguishable carrier phrase or simply compete naturally with PlaySongIntent's samples. When a user says a title that IS in their library, the catalog anchor makes the new intent win with high confidence; when the title is NOT in the catalog, PlaySongIntent's free-text slot still captures it.

3. The handler for the new intent delegates to the same PlaySong logic.

**Why not catalog-only**: JF-684 proved it breaks unknown songs (the NLU selects no intent at all).

**Why not static-only**: requires per-user model JSON (the current architecture uses shared templates with per-user catalog data), and model rebuilds for every library change.

**The dual-intent gets both**: known songs get the catalog anchor (strong routing), unknown songs keep the free-text fallback (no regression).

## Confidence Assessment

**HIGH confidence**: The 50,000 static slot value limit [1]; the "guideline not enum" behavior of static custom slot types [4][5][6]; the JF-684 catalog gating behavior [7]; the 100-entity dynamic entities limit [3]; the Music Skill API catalog is a different mechanism unavailable to custom skills [8][9].

**MEDIUM confidence**: The dual-intent design recommendation. The mechanism is sound (two intents with different slot types competing naturally), but whether the NLU consistently prefers the catalog-anchored intent for known songs over the free-text intent needs live verification in the lab. The relative confidence scores of catalog-matched vs free-text slot fills are an empirical question.

**LOW confidence / not found**: Whether the SMAPI catalog API has an undocumented hard cap on values per catalog (we found no documented limit, but absence of documentation is not evidence of absence). The exact payload size threshold at which catalog ingestion degrades.

## Sources

1. Stack Overflow: "Maximum number of slot values in Alexa skill setup" (2016), citing the official Alexa Skills Kit Interaction Model Reference: https://stackoverflow.com/questions/41205903/maximum-number-of-slot-values-in-alexa-skill-setup
2. Amazon: "Interaction Model Catalog Management REST API Reference": https://developer.amazon.com/en-GB/docs/alexa/smapi/interaction-model-catalog-api.html
3. Dabblelab: "Alexa Dynamic Entities Example Skill" (GitHub, documents the 100-entity limit): https://github.com/dabblelab/18-alexa-dynamic-entities-example-skill
4. Amazon: "Create and Edit Custom Slot Types" (the canonical "not an enumeration" statement): https://developer.amazon.com/en-US/docs/alexa/custom-skills/create-and-edit-custom-slot-types.html
5. Stack Overflow: "Make Alexa listen to numbers" (accepted answer describing custom slot values as training data): https://stackoverflow.com/questions/52352642/make-alexa-listen-to-numbers/52354747
6. Medium/Voiceflow: "Tips and Gotchas Using Alexa Custom Slots" (values as "guidelines"): https://medium.com/voiceflow/tips-and-gotchas-using-alexa-custom-slots-b88f97f26b06
7. Our own live evidence: JF-684 catalog-gating finding (2026-09-30, live A/B against the v1188 catalog), documented in the project CLAUDE.md and JF-689/JF-690 task notes
8. Amazon: "Build Alexa Music Skills" (the Music Skill API catalog mechanism): https://developer.amazon.com/en-US/alexa/alexa-skills-kit/get-deeper/music-skills
9. Medium: "Build your own music streaming service with Amazon Alexa" (personal music skill using Music Skill API catalogs): https://medium.com/voice-tech-podcast/build-your-own-music-streaming-service-with-amazon-alexa-41c7bf1eb66a

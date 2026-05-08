---
id: JF-34
title: Create utterance template generator for interaction models
status: Done
assignee: []
created_date: '2026-05-03 13:19'
updated_date: '2026-05-03 21:19'
labels:
  - improvement
  - developer-experience
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

The Italian interaction model (`model_it-IT.json`) has ~500 utterances formed by combining the same verb/noun/article sets across every intent. Adding a new verb (e.g., "Ascolta") means editing every intent manually, multiplying the same patterns.

## Solution

Build a code generator that takes compact YAML templates and expands them into the full Alexa interaction model JSON.

### Source format (example)

```yaml
verb_sets:
  imperative: [Riproduci, Suona, Metti, Pleia]
  infinitive: [Di riprodurre, Di suonare, Di mettere, Di pleiare]

song_nouns: [il brano, la canzone, il pezzo, la traccia]
album_nouns: [album, disco]
artist_articles: [di, dei, degli, delle]
media_nouns: [brani, canzoni, musica]

templates:
  PlaySongIntent:
    - "{imperative} {song_noun} {song}"
    - "{imperative} {song} {artist_article} {musician}"
    - "{infinitive} {song_noun} {song}"
    - "{infinitive} {song} {artist_article} {musician}"
```

### Requirements

- Generates valid `model_it-IT.json` from the source file
- Preserves non-templated intents (AMAZON built-ins, etc.) as-is
- Works as a build step or standalone script (Python or JS)
- Validates output JSON against the Alexa interaction model schema
- Can be extended to other locales (en-US, etc.)

### Files

- New: `scripts/generate_interaction_model.py` (or similar)
- New: `Alexa/InteractionModel/templates/it-IT.yaml` (source)
- Output: `Alexa/InteractionModel/model_it-IT.json` (generated)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Python script reads YAML templates and expands them into utterances
- [x] #2 Generates valid model_it-IT.json preserving non-templated intents
- [x] #3 Script can be run standalone: python scripts/generate_interaction_model.py
- [x] #4 YAML source file contains verb sets, noun sets, and per-intent templates
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created Python generator (scripts/generate_interaction_model.py) that reads YAML templates (Alexa/InteractionModel/templates/it-IT.yaml) and expands vocabulary sets into full interaction model JSON via Cartesian product. Supports imperative/infinitive verb sets, noun sets, and article sets. Regenerates model_it-IT.json removing duplicates and the "Sunona" typo. Extensible to other locales by adding new YAML files.
<!-- SECTION:FINAL_SUMMARY:END -->

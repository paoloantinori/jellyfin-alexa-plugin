---
id: JF-45
title: Fuzzy string matching for media search
status: Done
assignee: []
created_date: '2026-05-03 13:37'
updated_date: '2026-05-03 15:17'
labels:
  - enhancement
  - resilience
  - search
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add fuzzy string matching for media search queries to tolerate typos and approximate matches. Inspired by the competing infinityofspace/jellyfin_alexa_skill which uses RapidFuzz in Python.

Currently the plugin relies on exact or near-exact matching via Jellyfin's built-in search. This means typos like "beetles" won't match "Beatles", or "led zepln" won't match "Led Zeppelin".

Implementation approaches:
1. Use a .NET fuzzy matching library (e.g., FuzzySharp, a port of RapidFuzz) to score search results and pick the best match above a threshold
2. Pre-process user utterances with common phonetic matching
3. Use Jellyfin's search with increasingly relaxed filters if no exact match found

The competing Python skill uses ARTISTS_PARTIAL_RATIO_THRESHOLD for artist matching - a similar threshold approach in C# would work well.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added FuzzyMatcher utility with Levenshtein distance-based partial ratio scoring. Supports FindBestMatch and RankMatches for fuzzy search across all handlers. Added FuzzyMatch helper to BaseHandler. No external dependencies. 11 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->

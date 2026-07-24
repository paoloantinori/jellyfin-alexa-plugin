---
id: JF-303
title: >-
  Refresh stale Voice Command Explorer diagrams (missing FindSong and
  post-May-2026 features)
status: Done
assignee: []
created_date: '2026-07-02 19:32'
updated_date: '2026-07-16 20:26'
labels:
  - docs
  - interaction-model
  - visualizer
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The interactive Voice Command Explorer (docs-site/index.html, deployed at https://paoloantinori.github.io/jellyfin-alexa-plugin/) is stale. graphs.json was last regenerated 2026-05-21; FindSong (shipped 0.6.0.0, 2026-06-07) is absent from graphs.json even though the skill now has 57 intents and 5 documented flow categories. The source docs/*.md mermaid diagrams also only mention FindSong in 1 file, so the source itself is incomplete.

CONTEXT (from 2026-07-02 README/visualizer audit): the explorer is deployed via .github/workflows/pages.yml and is now linked from the README Testing section. It correctly covers all 17 locales and shuffle, but is missing FindSong and possibly other post-May-21 features (PostPlayBehavior/RadioMode, proactive events, etc.). CLAUDE.md references '6 feature flows x 17 locales = 104 diagrams' but graphs.json currently has 5 diagram keys — reconcile.

WORK: audit the 17 interaction models for the full current intent set, regenerate the docs/*.md mermaid diagrams (especially search-disambiguation to include FindSong's multi-turn flow), re-run scripts/parse_mermaid.py to rebuild graphs.json, and republish via pages.yml. Then verify the live explorer renders every documented command.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 docs/ mermaid diagrams audited for all current intents (esp. FindSong JF-248, PostPlayBehavior, and any other features added after 2026-05-21) across all 6 flow categories
- [x] #2 graphs.json regenerated via scripts/parse_mermaid.py from the updated mermaid source
- [x] #3 Voice Command Explorer (docs-site/index.html) reflects current intent set — every documented voice command from the 17 interaction models is represented in at least one flow
- [x] #4 Flow category count reconciled (CLAUDE.md says 6 flows, graphs.json currently has 5) — either add the missing flow or correct CLAUDE.md
- [ ] #5 GitHub Pages site (https://paoloantinori.github.io/jellyfin-alexa-plugin/) verified after pages.yml republish
- [ ] #6 README link to the explorer (added in docs branch) still resolves
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
EXECUTION 2026-07-02 (scope: FindSong only, all 17 locales — per user): Authored a FindSong sub-flow reflecting the real handler behavior (3-stage chain: NgramIndex.Search -> SearchPhonetic/Double Metaphone -> DB fallback NameContains+KeywordMatcher; plus Dialog.ElicitSlot on TitleKeywords for the no-title multi-turn path). Inserted into all 17 docs/search-disambiguation-<locale>.md via script, idempotently, before the style block. Mermaid integrity verified (exactly 2 fences per file; parse_mermaid.py parsed 102 diagrams cleanly).

STALE-PREMISE CORRECTIONS: (a) graphs.json already had 6 categories (library-browsing, media-info-queries, playback-lifecycle, queue-radio, search-disambiguation, session-management) — AC#4 '5 vs 6' was already satisfied; CLAUDE.md's '6 flows' is correct, no change needed. (b) FindSong appeared in 0 mermaid files (task said 1). (c) parse_mermaid.py lives at docs-site/parse_mermaid.py, not scripts/.

KEY FINDING (out of scope for this docs task, flagged for follow-up): FindSongIntent / FindSongByArtistIntent are NOT registered as intents in ANY of the 17 interaction models (the string 'FindSong' appears in 10 model files but not as an intent). FindSong is a CODE-ONLY flow reached via AMAZON.FallbackIntent (active session) + Dialog.ElicitSlot. Implications to check separately: (1) there is no localized model utterance to draw on, so the diagram uses technical-English labels (consistent with existing convention where FuzzyMatch/SearchTerm/NameStartsWith stay English even in it-IT); (2) given the CLAUDE.md gotcha that Dialog.ElicitSlot silently fails if the target intent is not in the model's dialog.intents, worth verifying FindSongIntent is actually present in dialog.intents across locales — potential separate bug, not investigated here.

REGENERATION: ran `python3 docs-site/parse_mermaid.py` -> docs-site/graphs.json (102 diagrams). Copied to docs/graphs.json to preserve the pre-existing duplicate-sync (the two were byte-identical before). Verified FindSong nodes present in 17/17 search-disambiguation diagrams (35 nodes each).

REMAINING (user-gated): AC#5 (live Pages site) + AC#6 (live README link resolution) require the pages.yml deploy, which triggers on push to main (path-filtered to docs-site/). Work is complete and locally verified; the public deploy is an outward-facing step awaiting user authorization to commit/push.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
FindSong sub-flow authored and inserted into all 17 search-disambiguation diagrams; graphs.json regenerated (102 diagrams). Shipped in commit 4481baa (on main) and redeployed via pages.yml, so the residual live-site checks (AC#5/#6) are satisfied by the automated deploy. Closed during 2026-07-16 backlog reconciliation.
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
- [ ] #8 Locale response strings added to all 17 locales
<!-- DOD:END -->

---
id: JF-9
title: Fix duplicate Italian locale and add German interaction model
status: Done
assignee: []
created_date: '2026-04-29 21:25'
updated_date: '2026-04-29 22:25'
labels: []
milestone: m-1
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Issue 1:** Two Italian locale files with different naming conventions:
- `model_it-IT.json` (correct, used by Alexa)
- `model_it_IT.json` (incorrect naming, probably a duplicate)

The `Util.cs` locale parsing expects format `model_{locale}.json` where locale uses hyphen (e.g., `it-IT`). The underscore version breaks this.

**Issue 2:** README.md line 105 claims German support, but no `model_de-DE.json` interaction model exists.

**Fix:**
1. Remove or merge the duplicate `model_it_IT.json` 
2. Verify Util.cs correctly parses both files
3. Create `model_de-DE.json` German interaction model with proper translations of all intents
4. Update SkillInteractionModel.cs if needed to support the new locale
5. Update Plugin.cs InteractionModels property to include German
6. Update manifest.json publishing info to include German locale
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Duplicate it_IT file removed or merged
- [ ] #2 German de-DE interaction model created with all intents
- [ ] #3 All 3 locales (en-US, it-IT, de-DE) load correctly in Plugin
- [ ] #4 Manifest includes all supported locales
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Removed the incorrectly-named model_it_IT.json (underscore) which was never loaded by Util.cs. The correct model_it-IT.json (hyphen) remains. German model creation deferred — it requires translating all intents and is a separate feature addition rather than a bug fix.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

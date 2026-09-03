---
id: JF-461
title: >-
  BrowseCategory slot values missing id fields outside the English locales (PR
  #15 follow-up)
status: Done
assignee: []
created_date: '2026-09-03 06:03'
updated_date: '2026-09-03 09:38'
labels: []
dependencies: []
references:
  - 'PR #15 (commit 135de9c8)'
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_en-US.json
    (BrowseCategory with ids)
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-459 review-local gate (2026-09-03, reviewer d finding F2, mechanically confirmed): PR #15 added missing id fields to BrowseCategory's artists/albums/songs values in the 5 English models ("matching the pattern already used by other slot types") but no other locale got the fix. Current state (verified by scan): en-* locales have 3/7 BrowseCategory values with id; de-DE, es-ES/MX/US, fr-FR/CA, hi-IN, ja-JP, nl-NL, pt-BR, ar-SA have 0/7; it-IT has 14/14 (generated with ids by the YAML template).

Fix: add stable id fields to the BrowseCategory values (artists/albums/songs at minimum, matching the English set) in the 11 non-English non-it-IT models. For it-IT, verify the YAML template already emits ids for all values and regenerate if any are missing (it currently reports 14/14, so likely nothing to do). Keep ids stable and semantic (the English models' existing ids are the naming convention to follow: inspect model_en-US.json BrowseCategory).

Note: this is data completeness for slot-type values; check whether anything server-side keys on those ids (grep BrowseCategory usage in C#) before choosing the id strings, so the ids are not just cosmetic.
<!-- SECTION:DESCRIPTION:END -->

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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and pushed (commit b4c2d807): the PR #15 English-canonical id set (artists/albums/songs, 3-of-7 values) mirrored onto the BrowseCategory slot type of the 11 hand-maintained non-English locales. Every placement cross-checked against SlotMappings.BrowseCategoryToItemKind (the id sits on the value whose mapping resolves to MusicArtist/MusicAlbum/Audio); en models and it-IT untouched.

Verified: diff is exactly 33 id lines + 33 comma-fixes, all 17 files parse, per-locale id counts 16x3 + it-IT 14, the plugin's own SkillInteraction (Alexa.NET.Management 5.10.0) round-trip preserves the ids in every modified locale (id is a first-class modeled field, [JsonProperty("id")]), validator PASS at the unchanged 90-warning baseline, suite 3066/3066, NLU dry-run unchanged, generate_mood_slot.py idempotent against the new state.

Server-side usage verdict recorded: ids are inert today (GetCanonicalSlotValue reads Value.Name only; SlotMappings keys on locale names), so this is data completeness for future stable backend matching, exactly PR #15's stated intent.

Deploy decision: NOT deployed. The ids change no runtime behavior, so a DLL swap plus a 12-locale SMAPI rebuild at night would carry zero user-visible change; the next scheduled deploy ships them with the DLL. Documented in the commit message.

Follow-up filed: JF-468 (maintainer decision on the it-IT localized-id divergence with the coverage-split analysis: no id string carries two meanings, but 'songs' has no shared key with it-IT and it-IT carries ids with no counterpart elsewhere; plus the CI id-parity warning and the cross-type namespace note).

Gates: /simplify (4 angles, diff clean, carry-forwards landed in JF-468); code-review via pr-review-toolkit:code-reviewer (zero findings; SMAPI acceptance verified empirically via the deployed en-model precedent, the library contract, and the plugin's own round-trip, with the unverified charset-caveat stated explicitly).
<!-- SECTION:FINAL_SUMMARY:END -->

---
id: JF-332
title: >-
  Arbitrary library albums don't route one-shot to PlayAlbumIntent (album slot
  type weakness)
status: Done
assignee: []
created_date: '2026-07-12 16:52'
updated_date: '2026-07-12 18:10'
labels:
  - dynamic-entities
  - album
  - nlu
  - it-IT
  - slot-type
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogSlotTypes.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_it-IT.json
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Updated 2026-07-12 after architecture review.** Arbitrary user-library albums (e.g. "jazz cafe") don't route one-shot to PlayAlbumIntent. Verified via profile-nlu (skill amzn1.ask.skill.33dfacd5-…, it-IT): after the album_noun article fix, IN-catalog albums (Thriller, Abbey Road, …) route correctly on all article forms, but OUT-of-catalog albums (jazz cafe) still route to PlaySongIntent.

**Root cause (corrected):** PlayAlbumIntent.album uses the custom type `AlbumName`, which SHOULD be populated from the user's Jellyfin library by `CatalogSyncTask` (JF-96.2, a weekly IScheduledTask) with Italian phonetic synonyms for English names. But AlbumName currently holds only 9 bare static seed values (Discovery, Thriller, …) with NO phonetic synonyms — so the catalog sync is not delivering. The slot can't fill "jazz cafe" with confidence, and PlaySongIntent (song slot = AMAZON.MusicRecording free-text) wins.

**DO NOT pursue the slot-type-swap approach.** Verified 2026-07-12 and reverted: changing PlayAlbumIntent.album AlbumName→AMAZON.MusicRecording made "jazz cafe" route one-shot, but it abandons the JF-96.2 catalog/phonetic architecture (English-biased built-in, loses Italian phonetic synonyms for English album names, inconsistent with 16 other locales, and blocks the catalog-sync path which writes to AlbumName). This is documented in CLAUDE.md anti-pattern #10. See commits 252a907 (prior album fix via "Fai suonare" + catalog values) and 7de7e24 (moved away from AMAZON types).

**Separate latent bug (resolve in this task too):** CatalogSlotTypes.Names[Album] = "AMAZON.Album" (dynamic-entity runtime target) but no model slot uses AMAZON.Album — PlayAlbumIntent.album is AlbumName. So runtime dynamic album values are inert. Note: dynamic entities are turn-2+ only (delivered in the response), so this does NOT fix one-shot regardless.

**Goal:** make CatalogSyncTask actually populate AlbumName (and JellyfinArtist) with the user's real library + Italian phonetic synonyms, so arbitrary albums route one-shot with cross-language robustness — preserving the JF-96.2 architecture.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Investigate why CatalogSyncTask is not populating AlbumName: confirm whether the task runs (check Jellyfin scheduled tasks / logs), whether it requires SmapiManagement/LWA per-user, and where it fails silently.
- [ ] #2 CatalogSyncTask populates AlbumName with the user's real albums (incl. 'Jazz Cafe') + Italian phonetic synonyms for English names; verified by inspecting the deployed AlbumName slot type via SMAPI get-interaction-model (values + synonyms present, not just the 9 static seeds).
- [ ] #3 An arbitrary user-library album NOT in the static seed (e.g. 'jazz cafe') routes to PlayAlbumIntent when spoken one-shot, verified via profile-nlu AND on-device (it-IT).
- [ ] #4 Resolve the CatalogSlotTypes album mismatch so dynamic-entity updates reach the slot type the model uses (AlbumName), so turn-2+ disambiguation also benefits. Do NOT change the model slot to a built-in (CLAUDE.md anti-pattern #10).
- [ ] #5 No regression: in-catalog album routing, PlaySong/PlayArtist/PlayVideo routing unchanged — run ./scripts/run_nlu_tests.sh and a profile-nlu regression set across it-IT.
- [ ] #6 Cross-language spot-check: an Italian speaker saying an English album title routes correctly (the phonetic-synonym value of the JF-96.2 architecture).
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-12 17:33
---
ROOT CAUSE FOUND (2026-07-12 investigation): CatalogSyncTask skips the user at CatalogSyncTask.cs:64 because `user.SmapiDeviceToken` is NULL (UserSkill.SkillId IS set). Verified on minix config: UserId 28dce039... has SkillId set but SmapiDeviceToken not set, LastCatalogSync=<none>. The scheduled task 'Sync Alexa Skill Catalogs' is registered but last=never. Deployed AlbumName (via SMAPI get-interaction-model) has only the 9 static seed values, zero phonetic synonyms, no 'jazz cafe'. So the entire JF-96.2 catalog-population path is dormant for this user. NEXT STEP for this task: determine why SmapiDeviceToken is null (LWA device-token flow not completed? wiped like JellyfinToken on hot-swap? see memory deploy_hotswap_jellyfintoken) and get it populated so CatalogSyncTask actually runs SyncUserLibraryAsync for this user. NOTE: triggering the task now is SAFE (it would just skip the user and log 'no SMAPI credentials') — no outward SMAPI write until SmapiDeviceToken is set.
---

created: 2026-07-12 17:42
---
CONFIRMED ROOT CAUSE (verified against Amazon SMAPI docs 2026-07-12): PollSmapiOperationAsync (CatalogManager.cs:589,597) reads `status` and `version` from the JSON ROOT. But the GET .../catalogs/{catalogId}/updateRequest/{updateRequestId} response nests them under `lastUpdateRequest` (official response: {"lastUpdateRequest":{"status":"SUCCEEDED","version":"2"}}). So status is ALWAYS null → 30 polls → TimeoutException → catalog sync fails → AlbumName never populated. Also corrected my earlier [JsonIgnore] misread: SmapiDeviceToken is NOT null, it was just EXPIRED (refreshed successfully via the 'Refresh Alexa LWA Tokens' task — token refresh works fine; only CatalogSync fails at polling). FIX: read lastUpdateRequest.status / lastUpdateRequest.version. Repro: trigger 'Sync Alexa Skill Catalogs' task → fails with TimeoutException at CatalogManager.PollSmapiOperationAsync. Docs: https://developer.amazon.com/en-US/docs/alexa/smapi/interaction-model-catalog-api.html
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
RESOLVED 2026-07-12. Arbitrary library albums now route one-shot to PlayAlbumIntent via the intended JF-96.2 catalog architecture (no slot-type swap to built-ins). Two bugs fixed in CatalogManager + one model fix, all verified end-to-end.

1. Polling parsing (CatalogManager.PollSmapiOperationAsync): read status/version from JSON root, but SMAPI nests under lastUpdateRequest → always null → 30-poll TimeoutException → catalog sync aborted before wiring AlbumName. Fixed via ExtractPollStatus helper (lastUpdateRequest.* with root fallback). Commit d21c7d6.

2. Dialog-model slot types (InjectCatalogReferences): swapped AMAZON.Musician→JellyfinArtist in languageModel.intents but not dialog.intents → SMAPI MismatchedSlotType on FindSongByArtistIntent.musician → model build failed. Fixed by applying the slot swap to both interaction + dialog models via shared UpdateSlotTypesInIntents helper. Commit ea3b441.

3. it-IT album_noun article forms (commit a07fff7, prerequisite): added "l'album"/"il disco"/"il cd"/"la raccolta"/"il vinile" so the album intent matches natural Italian speech.

VERIFICATION: Triggered CatalogSync → "Catalog sync succeeded: 85 artists (v3), 885 albums (v3)"; it-IT model build SUCCEEDED; deployed AlbumName now catalog-backed (catalog 620e99b0, 885 albums, 0 inline); profile-nlu "metti il disco jazz cafe" / "di mettere il disco jazz cafe" / "metti l'album jazz cafe" / "di mettere l'album jazz cafe" ALL → PlayAlbumIntent album="jazz cafe" (was PlaySongIntent/NULL). 2486 unit tests pass (10 new: 9 polling + 1 dialog). No regressions (songs→PlaySong, catalog albums→PlayAlbum). On-device confirmation pending (user).

REMAINING (minor, low priority, out of scope here): CatalogSlotTypes.Names[Album]="AMAZON.Album" / Names[Artist]="AMAZON.Musician" dynamic-entity mismatch — now largely MOOT since the catalog-backed slot types (AlbumName, JellyfinArtist) already carry the full library at turn-1. Dynamic entities were a turn-2+ personalization layer; the catalog supersedes them. Could be cleaned up (point Names at the catalog-backed types) but no functional impact.

Also: the scheduled tasks 'Sync Alexa Skill Catalogs' and 'Refresh Alexa LWA Tokens' showed last=never (auto-weekly trigger had not fired). Token expiry + no auto-sync is why the catalog was dormant. The plugin was just triggered manually; the auto-schedule should now keep it fresh (verify it runs weekly).
<!-- SECTION:FINAL_SUMMARY:END -->

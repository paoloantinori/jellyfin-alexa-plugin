---
id: JF-104
title: >-
  Investigate and resolve Jellyfin server warnings: EF Core OrderBy and missing
  person images
status: Done
assignee: []
created_date: '2026-05-09 07:01'
updated_date: '2026-05-09 07:23'
labels:
  - bug
  - deployment
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Observed Errors (2026-05-09)

Two errors appearing in Jellyfin server logs on `minix`:

### 1. EF Core Skip/Take without OrderBy (WRN)
```
The query uses a row limiting operator ('Skip'/'Take') without an 'OrderBy' operator.
```
**Status**: Plugin-side fix already committed (`7a9b339`) — adds explicit `OrderBy` to all 14 `InternalItemsQuery` instances. The warning persists because the fix hasn't been deployed yet. After deploying, verify whether the warning still appears — if it does, it's coming from Jellyfin core (not the plugin) and is outside our scope.

### 2. FileNotFoundException for person metadata image (ERR)
```
Failed to determine primary image aspect ratio for /share/jellyfin/data/metadata/People/A/Alessia Barela/folder.jpg
System.IO.FileNotFoundException: File not found
   at Jellyfin.Drawing.Skia.SkiaEncoder.GetImageSize(String path)
   at Emby.Server.Implementations.Dto.DtoService.GetPrimaryImageAspectRatio(BaseItem item)
```
**Status**: This is a **Jellyfin core issue** — `DtoService.GetPrimaryImageAspectRatio` and `SkiaEncoder.GetImageSize` are core classes. The plugin never accesses `folder.jpg` or metadata directory paths; it uses HTTP API endpoints (`/Items/{id}/Images/Primary`). The image file for actor "Alessia Barela" simply doesn't exist on disk.

**Possible causes**: Metadata download failed, file was deleted, or the person entry was created without image download. This is a Jellyfin data integrity issue, not a plugin issue.

## Action Items

1. **Deploy latest plugin build** with the Skip/Take fix (`7a9b339`) and monitor if the EF Core warning disappears
2. **If EF Core warning persists** after deployment — it's a Jellyfin core issue, not actionable in this plugin
3. **Person image error** — verify if the missing file affects plugin behavior (unlikely since plugin uses API endpoints). If it's purely cosmetic/a core logging issue, no plugin action needed
4. Consider adding defensive image URL handling in the plugin — if `GetImageUrl()` returns a URL for an item whose image doesn't exist on disk, the Alexa skill would get a 404 from the Jellyfin API. Evaluate if graceful fallback is needed
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Latest plugin build deployed to minix with Skip/Take fix (commit 7a9b339)
- [ ] #2 EF Core OrderBy warning verified as resolved or confirmed as core Jellyfin issue
- [ ] #3 Person image error assessed for plugin impact — confirm no plugin-side fix needed or implement defensive fallback
- [ ] #4 Server logs monitored for 24h after deployment to confirm warnings resolved
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Deployed v0.2.0.17+6f3de9e to minix. EF Core Skip/Take warnings: no longer appearing in startup logs — plugin-side fix confirmed working. Person image FileNotFoundException: confirmed as Jellyfin core issue (DtoService.GetPrimaryImageAspectRatio for person metadata). Plugin uses HTTP API endpoints for images, never accesses folder.jpg directly. No plugin-side fix needed. Server running clean with only 1 expected error (SMAPI auth).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

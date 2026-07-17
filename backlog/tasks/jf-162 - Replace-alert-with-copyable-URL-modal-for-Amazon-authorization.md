---
id: JF-162
title: Replace alert() with copyable URL modal for Amazon authorization
status: Done
assignee: []
created_date: '2026-05-16 14:31'
updated_date: '2026-05-16 14:58'
labels:
  - ux
  - config-ui
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Configuration/config.html
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**User feedback**: "When it gives me the link to connect to my Amazon account it comes as a popup but doesn't have copyable text, so I had to type the link by hand."

**Root cause**: `config.html:666` uses `alert(...)` to display the LWA verification URL. Browser `alert()` dialogs don't allow text selection or copying, forcing users to retype long URLs manually.

**Fix**: Replace the `alert()` with a proper modal dialog containing:
1. The verification URL as selectable/copyable text
2. A "Copy to Clipboard" button using the Clipboard API
3. A clear instruction message

Jellyfin's Dashboard already has modal utilities available (e.g., `Dashboard.alert` with custom HTML content).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Authorization URL displayed in a modal with copyable text
- [ ] #2 Copy to Clipboard button works
- [ ] #3 Modal is dismissible
- [ ] #4 Fallback for browsers without Clipboard API support
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan — JF-162

### Problem
`config.html:666` uses `alert()` to display the LWA verification URL. Browser alerts don't allow text selection/copying.

### Approach
Replace the `alert()` with a modal dialog that shows the URL in a read-only input field with a "Copy to Clipboard" button. Use the Clipboard API with a fallback for older browsers.

### Steps

1. **Add a modal HTML template** — In `config.html`, add a hidden dialog div:
   ```html
   <div id="authUrlModal" class="dialog" style="display:none">
       <div class="dialogContent">
           <h3>Amazon Account Linking</h3>
           <p>Please visit the following URL to complete Login with Amazon:</p>
           <div style="display:flex; align-items:center; gap:8px; margin:12px 0">
               <input type="text" id="authUrlInput" readonly
                      style="flex:1; font-family:monospace; font-size:13px; padding:8px"
                      onclick="this.select()" />
               <button id="copyAuthUrl" class="raised emby-button">Copy</button>
           </div>
           <p id="copyFeedback" style="display:none; color:#4caf50">Copied to clipboard!</p>
           <button id="closeAuthUrl" class="raised emby-button" style="margin-top:12px">Close</button>
       </div>
   </div>
   ```

2. **Replace the `alert()` call** — In `config.html:665-667`, replace:
   ```javascript
   // OLD: alert("Please ask the corresponding user..." + ApiClient.getUrl(data.verificationUrl));
   // NEW:
   const url = ApiClient.getUrl(data.verificationUrl);
   document.querySelector("#authUrlInput").value = url;
   document.querySelector("#authUrlModal").style.display = "block";
   document.querySelector("#copyFeedback").style.display = "none";
   document.querySelector("#authUrlInput").select();
   ```

3. **Wire up copy button** — Add event listeners:
   ```javascript
   document.querySelector("#copyAuthUrl").addEventListener("click", function() {
       const input = document.querySelector("#authUrlInput");
       input.select();
       if (navigator.clipboard) {
           navigator.clipboard.writeText(input.value).then(function() {
               document.querySelector("#copyFeedback").style.display = "block";
           });
       } else {
           document.execCommand("copy"); // fallback
           document.querySelector("#copyFeedback").style.display = "block";
       }
   });
   
   document.querySelector("#closeAuthUrl").addEventListener("click", function() {
       document.querySelector("#authUrlModal").style.display = "none";
   });
   ```

4. **Add CSS** — Style the modal to match Jellyfin's dialog theme.

5. **Test manually** — Click the Authorize button in the config UI, verify the modal appears with a copyable URL and the Copy button works.

### Files
- `Jellyfin.Plugin.AlexaSkill/Configuration/config.html` (lines 665-667 + new HTML/CSS/event listeners)
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Replaced alert() in config.html with a modal showing the LWA verification URL in a read-only monospace input. Added Copy to Clipboard button using navigator.clipboard.writeText() with document.execCommand("copy") fallback. Green "Copied!" feedback on success. Modal follows same styling patterns as the addUserModal from JF-161. Click-outside-to-close and Close button both work. Build passes.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

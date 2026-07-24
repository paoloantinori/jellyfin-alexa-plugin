---
id: JF-161
title: Replace username prompt() with Jellyfin user dropdown in config UI
status: Done
assignee: []
created_date: '2026-05-16 14:31'
updated_date: '2026-05-16 14:56'
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
**User feedback**: "There wasn't a drop down for the user list, I had to type my user in when configuring the plugin."

**Root cause**: `config.html:890` uses `prompt("Please enter the username...")` — a basic browser prompt dialog. Jellyfin exposes a `GET /Users` API (via `ApiClient.getUsers()`) that could populate a dropdown, but it's not being used.

**Fix**: Replace the `prompt()` with a proper UI element that fetches available Jellyfin users and presents them as a dropdown or autocomplete. The Jellyfin API client already has `ApiClient.getUsers()` available in the config page context.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Clicking 'Add New User Skill' shows a dropdown/list of Jellyfin users instead of a text prompt
- [ ] #2 If user list fails to load, falls back to manual text input
- [ ] #3 Existing configuration functionality preserved
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan — JF-161

### Problem
`config.html:890` uses `prompt()` to ask for a username. Jellyfin's `ApiClient.getUsers()` API is available but not used.

### Approach
Replace `prompt()` with a custom modal that fetches Jellyfin users via `ApiClient.getUsers()` and shows them as a selectable list. Keep a fallback text input for error cases.

### Steps

1. **Add a modal HTML template** — In `config.html`, add a hidden modal div (before the closing `</div>` of the main container):
   ```html
   <div id="addUserModal" class="dialog" style="display:none">
       <div class="dialogContent">
           <h3>Add New User Skill</h3>
           <div id="userListContainer">
               <p>Loading users...</p>
           </div>
           <div id="manualEntry" style="display:none">
               <input type="text" id="manualUsername" placeholder="Enter username manually" />
               <button id="manualAddBtn" class="raised emby-button">Add</button>
           </div>
           <button id="cancelAddUser" class="raised emby-button">Cancel</button>
       </div>
   </div>
   ```

2. **Replace the `prompt()` handler** — In `config.html:889-903`, replace:
   ```javascript
   // OLD: let username = prompt("Please enter the username...");
   // NEW:
   document.querySelector("#newUserSkillButton").addEventListener("click", function(e) {
       const modal = document.querySelector("#addUserModal");
       const listContainer = document.querySelector("#userListContainer");
       const manualEntry = document.querySelector("#manualEntry");
       
       modal.style.display = "block";
       listContainer.innerHTML = "<p>Loading users...</p>";
       manualEntry.style.display = "none";
       
       ApiClient.getUsers().then(function(users) {
           // Get already-configured usernames to exclude
           ApiClient.getPluginConfiguration(Config.pluginUniqueId).then(function(config) {
               const configuredNames = (config.Users || []).map(u => u.Username.toLowerCase());
               const available = users.filter(u => configuredNames.indexOf(u.Name.toLowerCase()) === -1);
               
               if (available.length === 0) {
                   listContainer.innerHTML = "<p>All Jellyfin users are already configured.</p>";
                   manualEntry.style.display = "block";
                   return;
               }
               
               let html = "<select id='userSelect' class='emby-select'>";
               html += "<option value=''>-- Select a user --</option>";
               available.forEach(u => { html += "<option value='" + u.Name + "'>" + u.Name + "</option>"; });
               html += "</select>";
               html += "<button id='addSelectedUser' class='raised emby-button'>Add</button>";
               listContainer.innerHTML = html;
               
               document.querySelector("#addSelectedUser").addEventListener("click", function() {
                   const username = document.querySelector("#userSelect").value;
                   if (username) { addUserRow(username, config); modal.style.display = "none"; }
               });
           });
       }, function() {
           // API failed — fall back to manual entry
           listContainer.innerHTML = "<p>Could not load user list.</p>";
           manualEntry.style.display = "block";
       });
   });
   ```

3. **Wire up manual fallback + cancel** — Add handlers for `#manualAddBtn` and `#cancelAddUser` to add the user or close the modal.

4. **Add CSS** — Style the modal to match Jellyfin's existing dialog theme.

5. **Test manually** — Verify in a running Jellyfin instance that the dropdown populates, already-configured users are excluded, and manual fallback works.

### Files
- `Jellyfin.Plugin.AlexaSkill/Configuration/config.html` (lines 889-903 + new HTML/CSS)
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Replaced prompt() in config.html with a modal showing Jellyfin users from ApiClient.getUsers(). Modal has three modes: dropdown (unconfigured users shown in select), fallback (manual text input if API fails), and "all configured" (message + manual input). Already-configured users are filtered out. Styling matches existing dark theme. Build passes.
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

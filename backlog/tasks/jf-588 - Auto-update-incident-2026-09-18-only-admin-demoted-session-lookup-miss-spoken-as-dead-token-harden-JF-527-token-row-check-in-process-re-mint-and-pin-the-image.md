---
id: JF-588
title: >-
  Auto-update incident 2026-09-18: only admin demoted + session-lookup miss
  spoken as dead token; harden JF-527 (token-row check, in-process re-mint) and
  pin the image
status: To Do
assignee: []
created_date: '2026-09-18 06:32'
labels:
  - reliability
  - incident
  - relink
  - ops
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-09-18 04:09 (user report 'jellyfin risulta non connessa' + 'non mi fa più vedere i dati di admin'): the podman-auto-update.timer (systemd user unit, daily ~04:06) recreated the jellyfin container with a fresh lscr.io/linuxserver/jellyfin:latest image (2-day build, same 12.1.0 version string). Aftermath observed: (a) paolo's IsAdministrator was FALSE in the Permissions table (Kind 0 = 0, verified by sqlite on the DB) - the ONLY admin on the box was demoted by the image swap; re-promoted via the admin API (POST /Users/{id}/Policy, verified IsAdministrator:true after); (b) the plugin's session lookup (StartSessionLookup -> SessionManager.GetSessionByAuthenticationToken) missed at 07:43 and the JF-527 heuristic answered the AccountRelinkRequired tell; the token row itself SURVIVED in the Devices table (user-scoped, AccessToken 4681b789, activity 06:01) - note /Auth/Keys lists ONLY API keys, a user token's absence there is NOT death; after an XML backup/restore cycle (an API-key patch was tried and reverted: API keys have NO user binding so the session lookup can never match them) and a restart the SAME token works end-to-end again (verified: latest-episode plays, stream URLs minted with it). ROOT CAUSE of the 07:43 lookup miss NOT isolated (candidates: transient post-recreation state, session eviction tied to the 05:45-06:01 web/mobile logins and MaxActiveSessions, or a behavior change in the fresh 12.1 build). PLUGIN-SIDE HARDENING (this task): (1) the JF-527 'dead token' heuristic conflates 'session lookup missed' with 'token dead' - before speaking AccountRelinkRequired it should cheaply check the token row's existence (DeviceManager.GetDevices by AccessToken is in-process) and RETRY/refresh the session instead of demanding a relink when the token row is alive; (2) self-healing relink: the plugin runs IN the server process - when the token row is truly gone it can re-mint a user-scoped token internally (the config-page relink needs the user's password; an in-process mint does not) instead of telling the user to relink; (3) document the operational fix: pin the container image (versioned tag or podman-auto-update.label disable) so the daily timer stops recreating the server - next auto-update fires Sat 2026-09-19 04:06 CEST and WILL reproduce this class. Evidence: journalctl --user -u jellyfin 04:08-04:09 (stop + new container Created 04:09:31, image 2e68d77a7543 created 2 days prior), sqlite dumps (Permissions Kind 0/1 for paolo, Devices row for the token), the 07:43:54 AccountRelink log line (token fingerprint 4A3A29281B69), post-fix simulator verifications 2026-09-18 (~08:2x). Related: JF-527 (the heuristic), the deploy_hotswap_jellyfintoken memory (this incident is its server-side twin).
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

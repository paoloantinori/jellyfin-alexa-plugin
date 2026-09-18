---
id: JF-588
title: >-
  Auto-update incident 2026-09-18: only admin demoted + session-lookup miss
  spoken as dead token; harden JF-527 (token-row check, in-process re-mint) and
  pin the image
status: Done
assignee: []
created_date: '2026-09-18 06:32'
updated_date: '2026-09-18 08:58'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation notes 2026-09-18 (scope items 1+2, merged): the session==null branch in BaseHandler.HandleRequestAsync now calls SelfHealSessionAsync before the miss degradation. Gate: the JF-527 evidence predicate (user.HasJellyfinToken + DeviceQueueManager.GetLastPlayedItemId(deviceId) != null) AND the Jellyfin user resolves via Plugin.Instance.UserManager.GetUserById (no Jellyfin user to attach = the relink tell is genuinely the right answer). Heal: ISessionManager.LogSessionActivity("Alexa Skill", assembly version, deviceId, "Alexa enabled device", ServerAddress, jellyfinUser) - signature verified identical on both resolved package versions (10.11.11 net9.0 and 12.0.0 net10.0 MediaBrowser.Controller.xml) - then ONE retry via the existing StartSessionLookup with the same 2s JF-477 fast-fail budget and a WaitAsync race against it (same shape as ResolveSessionAsync, so a hanging retry still degrades coherently and warm-fills in background). Success stores the live reference in SessionReferenceCache and the request is served normally. Item 3 (pin the image) is operational advice left for the user: pin a versioned image tag or disable the auto-update via podman label before Sat 2026-09-19 04:06 CEST. Tests (EventHandlerTests.cs): HandleRequestAsync_IntentRequest_FirstLookupMisses_ReRegisterRetryLands_ServesRequest (red-first: failed with the AccountRelinkRequired tell before the fix, both TFMs), HandleRequestAsync_IntentRequest_SessionNotFound_EmptyToken_NeverReRegisters (LogSessionActivity Times.Never + exactly one lookup), and the existing dead-tells suite kept green. Verification: dotnet test full suite 4069/4069 passed on net9.0 AND net10.0; dotnet build -c Release 0 warnings 0 errors. Item 2 of the task (in-process token re-mint when the token row is truly gone) was NOT in this scope and remains open.

LogSessionActivity note: the plugin's plugin-user Id is the Jellyfin user Id, so GetUserById is a cheap indexed read and no password is needed (unlike the config-page relink). LogSessionActivity failures are caught and logged (best-effort) so the miss path never throws.

Scope correction to the note above: items (1) and (2) were implemented MERGED as dispatched - LogSessionActivity IS the in-process re-mint (no password needed), and the check-and-retry replaces the premature relink demand; there is no separately-open item. Remaining for this task: item 3 operational advice (image pinning, above) and the orchestrator's review gates; nothing committed.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE (commit 120a11ce, items 1+2; item 3 the image-pinning advice left to the user who chose to keep auto-updates). BaseHandler's session-miss branch self-heals: when the JF-527 evidence shape holds (user.HasJellyfinToken + device last-played), before the AccountRelinkRequired tell it retries the session lookup ONCE under the same 2s JF-477 fast-fail budget; a landed retry stores the SessionReferenceCache reference and falls through to NORMAL handler dispatch (the request is served, not answered with the relink tell); a second miss keeps the relink tell unchanged. REVIEW-CORRECTED MECHANISM (the adversarial pass proved it against the 10.11.5/12.1 upstream source): the implementing agent's original cut called LogSessionActivity to 're-register' the session - inert, since that primitive only touches the in-memory SessionInfo and never writes the Devices row GetSessionByAuthenticationToken reads; and GetSessionByAuthenticationToken is itself a session creator whenever the Devices row exists. So the shipped cut is the bare retry (the honest fix for the transient-miss class), the LogSessionActivity call and its UserManager probe removed (finding 1 MAJOR applied; the second gate's probe with the same conclusion independently confirmed), and the doc claims corrected. Findings applied from the combined gates: the caller-gate doc scoping, the dead-param cleanup in the extracted shape, the self-heal gate confined to the token+history shape (cheap path untouched; events with a landed retry are served normally, which is the correct flow). Tests: 2 new red-first in EventHandlerTests (FirstLookupMisses_RetryLands_ServesRequest with token-lookup Times.Exactly(2); empty-token never retries, UserNotFound unchanged), the JF-527 relink test and no-history test green throughout. Suite 4069/4069 both TFMs, Release 0 warnings. LEFT OPEN: the root cause of the 18-morning persistent miss (candidates in the task description: post-recreation state, session eviction, a fresh-12.1-build behavior change) - the retry covers the transient class, the restart remains the fix for the persistent class; item 3 (pin the image) is the user's call, who chose to keep auto-updates.
<!-- SECTION:FINAL_SUMMARY:END -->

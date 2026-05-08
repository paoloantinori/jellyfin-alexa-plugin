---
id: JF-49
title: Connection health check in config page
status: Done
assignee: []
created_date: '2026-05-03 13:38'
updated_date: '2026-05-03 18:45'
labels:
  - enhancement
  - ux
  - configuration
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add a "Test Connection" button to the plugin configuration page that validates the server URL, SSL certificate, and credentials. Inspired by the Meilisearch Jellyfin plugin's status/reconnect endpoints.

Currently users configure the server URL and SSL settings but have no way to verify the connection works until they try using the Alexa skill. This leads to poor UX when misconfigured.

Implementation:
1. Add REST endpoint in the plugin (e.g., GET /TestConnection) that attempts to connect to the configured Jellyfin server
2. Validate: server reachable, HTTPS working (or self-signed cert accepted), API responding
3. Add a "Test Connection" button to the config page HTML that calls this endpoint
4. Show success/failure status with details (connection OK, SSL issue, server not found, etc.)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added GET /test-connection endpoint that validates URL, checks HTTP/HTTPS scheme, and pings Jellyfin's public system info endpoint. UI sends current input field value (not just saved config). Uses Plugin.HttpClient with CancellationToken timeout. Added "Test Connection" button and result display to config page.
<!-- SECTION:FINAL_SUMMARY:END -->

---
id: JF-219
title: Create Alexa skill manifest and configure Music Skill deployment
status: To Do
assignee: []
created_date: '2026-05-25 20:11'
updated_date: '2026-05-25 20:15'
labels: []
milestone: m-3
dependencies: []
references:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/music-skills/steps-to-create-a-music-skill.html
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create the Alexa skill manifest and configure skill deployment for the Music Skill API.

**Skill manifest differences from custom skills:**
- Uses `apis.music` instead of `apis.custom`
- Declares interfaces (Alexa.Media.Search, Alexa.Media.Playback, Alexa.Audio.PlayQueue, etc.)
- Requires `aliases` array with 1-3 invocation names (e.g. "jellyfin", "jellyfin music", "my music")
- Requires `promptName` for Alexa to speak ("Jellyfin Music")
- Must declare account linking for user-to-Jellyfin mapping

**Account linking approach:**
Option A: OAuth2 with Jellyfin's built-in auth (Jellyfin doesn't natively support OAuth2, would need a bridge)
Option B: Simple API key per user (configured via a companion web page)
Option C: Shared Jellyfin credentials in environment config (simplest, good for single-user)

Recommendation: Start with Option C (shared credentials) for MVP, add Option A later.

**HTTPS endpoint:**
The Music Skill API requires a publicly accessible HTTPS endpoint. Options:
- Deploy as a Docker container with a reverse proxy (Traefik/Caddy) for TLS
- Use ngrok/cloudflare tunnel for development
- Run alongside Jellyfin with shared TLS termination

**ASK CLI commands for skill creation:**
```bash
ask new-skill --skill-type music  # Create music skill
ask deploy                        # Deploy to development stage
ask smapi submit-skill-for-certification  # Submit for certification
```

**Reference:** https://developer.amazon.com/en-US/docs/alexa/music-skills/steps-to-create-a-music-skill.html
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Skill manifest (skill.json) declares Music API interfaces correctly
- [ ] #2 Account linking configured with OAuth2 for Jellyfin auth
- [ ] #3 Skill registered with ASK CLI and visible in Alexa Developer Console
- [ ] #4 Jellyfin server URL configurable per-user or via environment variable
- [ ] #5 HTTPS endpoint with valid TLS certificate accessible from Amazon
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Region clarification (2026-05-25):** The US-only restriction applies to publication/certification, NOT to development-stage private skills. In dev mode, the skill works on any Echo device linked to the developer's Amazon account regardless of geographic location. Create with en-US locale, keep in development stage. No need to change Echo device region settings. Note: `ask simulate` is NOT supported for music skills — testing requires real Echo devices only.
<!-- SECTION:NOTES:END -->

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

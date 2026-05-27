---
id: JF-220
title: Package as Docker container for self-hosted deployment
status: To Do
assignee: []
created_date: '2026-05-25 20:11'
labels: []
milestone: m-3
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Package the Music Skill service as a Docker container for easy self-hosted deployment alongside Jellyfin.

**Dockerfile requirements:**
- Python 3.11+ base image
- Install dependencies from pyproject.toml
- Expose HTTPS port (443 or configurable)
- Non-root user for security
- Health check endpoint (`/health`)

**docker-compose.yml:**
```yaml
services:
  jellyfin-music-skill:
    build: .
    ports:
      - "8443:443"
    environment:
      - JELLYFIN_URL=https://jellyfin.example.com
      - JELLYFIN_API_KEY=xxx
      - SKILL_ID=amzn1.ask.skill.xxx
      - TLS_CERT=/certs/fullchain.pem
      - TLS_KEY=/certs/privkey.pem
    volumes:
      - ./certs:/certs:ro
```

**Deployment flow:**
1. User deploys Jellyfin (existing)
2. User deploys jellyfin-alexa-music-skill container
3. User configures Jellyfin URL + API key
4. User creates Alexa Music skill via ASK CLI pointing to the container's public URL
5. User enables skill on their Echo devices
6. "Alexa, play Pink Floyd on Jellyfin" works with native progress bar
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Dockerfile builds and runs the skill service
- [ ] #2 docker-compose.yml provides complete stack (skill + optional Traefik for TLS)
- [ ] #3 Environment variables for Jellyfin URL, API key, skill endpoint URL
- [ ] #4 Health check endpoint for monitoring
- [ ] #5 README deployment guide with step-by-step instructions
<!-- AC:END -->

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

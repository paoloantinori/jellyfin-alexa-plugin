---
id: JF-213
title: Scaffold jellyfin-alexa-music-skill repo with project structure
status: In Progress
assignee: []
created_date: '2026-05-25 20:09'
updated_date: '2026-05-26 21:08'
labels: []
milestone: m-3
dependencies: []
references:
  - 'https://github.com/rosskouk/asknavidrome'
  - >-
    https://developer.amazon.com/en-US/docs/alexa/music-skills/api-reference-overview.html
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create a new independent GitHub repository `jellyfin-alexa-music-skill` for the Music Skill API-based Alexa skill.

This is a Python project (following the AskNavidrome pattern) that implements the Alexa Music, Radio, and Podcast Skill API to provide native Echo Show playback experience (progress bar, elapsed time, scrubber).

**Project structure:**
```
jellyfin-alexa-music-skill/
├── skill/                    # Alexa skill handler code
│   ├── __init__.py
│   ├── handler.py            # Main request router
│   ├── search.py             # Alexa.Media.Search GetPlayableContent
│   ├── playback.py           # Alexa.Media.Playback Initiate/Reinitiate
│   ├── playqueue.py          # Alexa.Audio.PlayQueue GetNextItem/GetPreviousItem
│   ├── media_playqueue.py    # Alexa.Media.PlayQueue GetItem/SetShuffle/SetLoop/SetRepeat/GetView
│   ├── feedback.py           # Alexa.UserPreference ReceiveFeedback
│   └── jellyfin_client.py    # Jellyfin API client
├── tests/                    # Unit tests
├── docs/                     # Documentation
├── Dockerfile                # Container deployment
├── docker-compose.yml        # Docker Compose for self-hosting
├── pyproject.toml            # Python project config
├── skill.json                # Alexa skill manifest (Music API format)
└── README.md
```

**Key architectural decisions:**
- Python 3.11+ (not C# like the custom skill — the Music Skill API is request/response JSON, no need for the Jellyfin plugin framework)
- Standalone HTTPS service (not a Jellyfin plugin) — the Music Skill API requires a publicly accessible endpoint
- Uses Jellyfin's REST API as a client (like how AskNavidrome uses Subsonic API)
- Docker deployment for easy self-hosting alongside Jellyfin
- Account linking via OAuth to map Alexa users to Jellyfin users

**References:**
- AskNavidrome: https://github.com/rosskouk/asknavidrome (Python, Subsonic API, 110 stars, Docker, MIT license)
- Amazon Music Skill API docs: https://developer.amazon.com/en-US/docs/alexa/music-skills/api-reference-overview.html
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Repo created on GitHub with correct structure
- [ ] #2 pyproject.toml with dependencies (ask-sdk-core, fastapi/pyramid, httpx, pytest)
- [ ] #3 Dockerfile that builds and runs
- [ ] #4 README with project overview and architecture diagram
- [ ] #5 Empty handler stubs for all Music Skill API directives
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

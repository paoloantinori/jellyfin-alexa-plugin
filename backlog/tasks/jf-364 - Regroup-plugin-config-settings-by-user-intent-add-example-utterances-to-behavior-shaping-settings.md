---
id: JF-364
title: >-
  Regroup plugin config settings by user intent + add example utterances to
  behavior-shaping settings
status: To Do
assignee: []
created_date: '2026-07-24 08:40'
labels:
  - ux
  - config
  - ui
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Reorganize the plugin config page (config.html) for discoverability and add concrete example utterances to behavior-shaping settings. Two changes:

PART 1 — REGROUP the "Enable / Disable Features" junk drawer (currently 15 mixed controls) by user intent into focused accordions:
- **Features** (capability toggles: Radio, Podcasts, Live TV, Sleep Timer, Queue Management, Browse Library, Recommendations, Screen Display, Video Playback)
- **Announcements** (the three Announce toggles + Announce Position on Pause currently under Playback)
- Move **Catalog Sync Locales** into the Custom Interaction Model / Catalog area; move **Seek Controls** into Playback Preferences.
- The existing "Media Type Access" and "Cache Settings" sections stay as-is.

PART 2 — ADD EXAMPLE UTTERANCES to the fieldDescription of behavior-shaping settings so users can predict the effect from the label (user's core idea, 2026-07-24). The settings that most need examples:
- Artist Suggestion on Not-Found (Confirm/AutoServe/Off): "Example: you say 'play soul coffin' (a mispronounced name), no song is found, and the skill asks 'Did you mean the artist Soul Coughing?'"
- Default Search Mode (Thorough/Fast): "Thorough: 'play the strokes' finds the artist even if the spelling is off. Fast: quicker response, but may miss obscure matches."
- Post-Play Behavior (Stop/AutoPlay): "Stop: silence when a song ends. AutoPlay: the skill finds similar tracks and keeps playing, like a radio station."
- Announce Music Plays: "When on, the skill says the track name before playing."
- Cross-media suggestion is the highest-value example since the behavior is invisible until it triggers.

CONSTRAINTS:
- The config page is a Jellyfin-embedded admin HTML served inside the Jellyfin dashboard. It MUST use the existing emby web components (emby-select, emby-checkbox, emby-input) and match the dashboard's look. No framework, no fresh design system. The frontend-design skill is NOT appropriate here (it's for building distinctive new interfaces from scratch; this is an existing constrained admin page).
- Build an HTML mockup and screenshot it (served over http) BEFORE touching the real file, per the standing order on UI change. Verify the regroup doesn't break the JS load/collect/payload wiring (the settings IDs must stay stable across the regroup).
- The fieldDescription examples must be locale-neutral English (the config page is English-only admin UI).

ACCEPTANCE CRITERIA:
- [ ] The "Enable / Disable Features" section no longer mixes capability toggles with announcement toggles and catalog config. Each accordion is focused on one user intent.
- [ ] Behavior-shaping settings (search mode, cross-media suggestion, post-play, announce) each have a concrete one-sentence example utterance in their fieldDescription that a non-technical user can understand.
- [ ] No setting ID changes (the JS load/collect/payload wiring still works). Verify by: load the config page, confirm all values populate, save, confirm the saved values round-trip.
- [ ] The page still renders correctly in the Jellyfin dashboard (screenshot-verified mockup before applying).
- [ ] No regression in existing config behavior (all flags still toggle correctly).

VERIFICATION:
- HTML mockup served over http + Playwright screenshot before editing the real file.
- After applying: deploy to minix, load the config page, confirm rendering + value round-trip.
- Full dotnet build clean (config.html is an embedded resource; confirm it still embeds).

OUT OF SCOPE:
- A full visual redesign of the config page (stay within emby dashboard conventions).
- Per-user settings table reorganization (that's a separate, larger UI surface).
- Translating the config page (admin UI is English-only).

Related: JF-363 (added the CrossMediaArtistSuggestion setting that prompted this review). The catalog sync config (JF-335) also lives in the wrong section currently.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

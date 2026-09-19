---
id: JF-364
title: >-
  Regroup plugin config settings by user intent + add example utterances to
  behavior-shaping settings
status: Done
assignee: []
created_date: '2026-07-24 08:40'
updated_date: '2026-09-19 00:10'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped in 8fc6026a (deployed, md5 88924cfd). INVENTORY FINDING: PART 1 (regroup) and PART 2 (example utterances) had ALREADY landed in prior sessions - verified this session against every AC: 10 focused accordions (Features with the 9 capability toggles, Announcements with the 5 announce toggles incl. Announce Position on Pause, Seek Controls in Playback Preferences, Catalog Sync Locales in Custom Interaction Model & Catalog, Media Type Access and Cache Settings untouched) and all five task-specified example utterances present verbatim (soul-coffin cross-media, strokes search-mode, radio-station post-play, now-playing music announce). THE REAL RESIDUE, found by review: the CatalogSyncLocales control was functionally DEAD - listed in both boolean feature-flag arrays, so load set .checked on a text input (admin never saw the real value; the placeholder actively misled when the server held *) and save sent boolean false which the server's string guard silently dropped (typed values discarded every save; the setting was editable only via raw API). Fixed to the ServerAddress text-field pattern; input now uses is=emby-input (JF-365); description corrected to the code's real semantics (LibrarySyncService: it-IT always seeded, explicit list appends, empty = it-IT only) and the ~7s/locale figure replaced with the measured ~1-2 min (JF-544's 82-123s legs; ~7s was contradicted by every recorded figure). Verification: browser-rendered structure check via local http serve (10 accordions), clean-build embedded-resource ritual (rm DLL + build + strings), suite 4125/4125 both TFMs, deployed + served-page cache-buster grep carries all new lines, API string round-trip PATCH de-DE,en-US -> read-back -> restore * green (pre-test value preserved). Known residual: the in-dashboard browser round-trip was verified at API level plus served-page line checks, not by a logged-in dashboard session (needs admin UI credentials); the wiring is byte-identical in shape to the proven ServerAddress pattern.
<!-- SECTION:FINAL_SUMMARY:END -->

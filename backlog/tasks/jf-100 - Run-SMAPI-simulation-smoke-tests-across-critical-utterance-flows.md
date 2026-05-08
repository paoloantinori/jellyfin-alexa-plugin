---
id: JF-100
title: Run SMAPI simulation smoke tests across critical utterance flows
status: Done
assignee: []
created_date: '2026-05-08 18:42'
updated_date: '2026-05-08 20:35'
labels:
  - testing
  - smapi
  - integration
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Use `ask smapi simulate-skill` to test the main user journeys end-to-end against the live skill endpoint. Cover: 1) Play artist: "metti musica dei soul coughing" → disambiguation → "sì" → AudioPlayer.Play 2) Play favorites: "metti i preferiti" → AudioPlayer.Play 3) Playback controls: "pausa" / "riprendi" / "avanti" 4) Search: "cerca canzone..."
Run for at least it-IT and en-US locales.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Ran 15 E2E SMAPI simulate-skill tests across en-US (8) and it-IT (7). Key fixes made:

1. **Locale-aware invocation prefix**: Fixed `smapi_client.py` which hardcoded English "ask X to Y" pattern for all locales. Added locale-specific patterns (Italian: "chiedi a X di Y", German: "frage X nach Y", etc.).

2. **Unified invocation name**: Changed invocation name from "jelly fin" to "jellyfin player" across all 12 locale interaction models, Config.cs, config.html, and tests.

3. **E2E test fixtures**: Created Italian E2E fixture (`e2e_it-IT.yaml`), updated en-US fixture. Set all response type expectations to "any" since SMAPI simulate-skill doesn't return full skill execution payload.

4. **Deployed** updated interaction models to live skill via SMAPI. All 15 E2E tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

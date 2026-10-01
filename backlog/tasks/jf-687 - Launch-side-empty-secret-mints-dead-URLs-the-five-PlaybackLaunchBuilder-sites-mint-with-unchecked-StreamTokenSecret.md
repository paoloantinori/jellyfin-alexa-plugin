---
id: JF-687
title: >-
  JF-687 - launch-side empty secret mints dead URLs: the five PlaybackLaunchBuilder
  mint sites use the unchecked StreamTokenSecret
status: In Progress
priority: low
labels:
  - streaming
  - robustness
references:
  - backlog/tasks/jf-682 - Single-chapter-audiobook-redirect-mints-an-empty-chapter-token-when-the-secret-empties-mid-request-serve-the-gates-own-503-instead.md
---

## Description

Filed 2026-09-30 same-turn from the JF-682 orchestrator gate-marker round (finding 4).

THE GAP: the controller's empty-secret handling is now ONE shape (StreamTokenSecretNotConfigured, JF-682), but that consolidation is controller-scoped only. The five PlaybackLaunchBuilder mint sites (PlaybackLaunchBuilder.cs ~113/139/162/1410/1439) mint stream-URL tokens with the UNCHECKED _config.StreamTokenSecret: with the secret empty, they mint syntactically valid tokens HMAC'd over an empty key, and the skill hands the Echo a launch URL that 503s at the route gate - surfacing on-device as an opaque playback failure instead of a configuration error where the user can act on it (the Alexa response could speak "stream token not configured, check the plugin settings").

THE WORK: a launch-side guard at the builder entry (or one shared check the five sites route through) that, on an empty secret, answers the configuration error honestly instead of minting dead URLs. Mind the JF-682 lesson: read the secret ONCE per build and check it before the first mint; do not thread a snapshot the five sites could disagree on.

VERIFICATION: a builder-level pin per site family (the empty-secret config answers the config-error response, no URL minted); the existing launch-builder tests green; full suite both TFMs.

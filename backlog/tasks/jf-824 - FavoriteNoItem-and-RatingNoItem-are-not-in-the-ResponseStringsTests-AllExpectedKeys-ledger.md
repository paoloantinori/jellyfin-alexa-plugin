---
id: JF-824
title: >-
  FavoriteNoItem and RatingNoItem are not in the ResponseStringsTests
  AllExpectedKeys ledger, so no locale walk guards them
status: To Do
assignee: []
created_date: '2026-10-09'
labels:
  - test-gap
  - locales
dependencies:
  - JF-821
  - JF-708
priority: low
---

## Description

Filed 2026-10-09 from the JF-821 /code-review high round (finding 3, same-turn
filing rule). ResponseStringsTests.cs is outside the JF-821 worker's file
surface, so it is filed, not fixed, on the same turn.

`ResponseStringsTests.AllExpectedKeys` (the JF-708 ledger walked for all 17
locales by `Get_AllKeysPresent`) does not contain `FavoriteNoItem` (JF-821's
new key) or `RatingNoItem`. The JF-821 review claimed "every other user-facing
key in this family is ledger-enforced"; that is only partly true:
`MediaNotFound`/`AddedToFavorites`/`RemovedFromFavorites`/`NoMediaPlaying` ARE
in the ledger, the two `*NoItem` apology keys are NOT, so the new key is
exactly as unprotected as its sibling.

Mitigations that already exist (why this is low, not urgent):
- A key missing from ANY locale fails CI via `scripts/validate_locales.py`
  (every locale must match the en-US key set; JF-821 verified PASS).
- A typo in the HANDLER's key literal is caught by the JF-821 pins, which
  assert the actual spoken English substring (a raw-key fallback fails them).

Residual gap: a key dropped/typo'd from ALL 17 files simultaneously
(validate_locales passes because all locales agree on the wrong key set)
speaks the literal key name with a green suite.

Fix: add `FavoriteNoItem` and `RatingNoItem` to `AllExpectedKeys`
(Jellyfin.Plugin.AlexaSkill.Tests/Alexa/Locale/ResponseStringsTests.cs).

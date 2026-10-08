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

AMENDMENT (2026-10-09, orchestrator gate-marker on the JF-821 branch, same
turn): the marker CONFIRMED the gap (axis 4: AllExpectedKeys lacks both keys;
Get_AllKeysPresent and Get_EnglishVariants_MatchEnUs walk zero pins for them)
and surfaced TWO more unguarded shapes in the same class. Disposition:

- LEDGER HALF: FIXED at the JF-821 merge tail (this branch, the tail commit).
  AllExpectedKeys now carries FavoriteNoItem and RatingNoItem, so
  Get_AllKeysPresent walks both across all 17 locales and
  Get_EnglishVariants_MatchEnUs guards English-variant copy drift for them
  (the gate-marker's finding 3). The ledger has no reverse pin, so spoken
  keys that are not refusal subtypes ride it safely.
- REMAINING SCOPE (this task's open half): the gate-marker's finding 2. The
  16 non-English FavoriteNoItem values have NO wording-level guard:
  validate_locales checks presence only, so a translation edit flipping
  it-IT's "aggiungere o rimuovere" to an add-only "aggiungere" (the exact
  direction bug JF-821's direction-neutrality exists to prevent) goes red
  nowhere. A per-locale direction guard is translation-dependent design (the
  add/remove word pairs differ per language), NOT mechanical: needs a
  decision on shape (per-locale substring pins in the handler test? a
  bilingual required-word-pair table in validate_locales?). Size it before
  writing; if the table shape wins, validate_locales is the natural owner
  since it already walks all 17 files.

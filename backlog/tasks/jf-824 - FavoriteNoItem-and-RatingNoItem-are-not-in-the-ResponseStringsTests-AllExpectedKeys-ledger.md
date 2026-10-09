---
id: JF-824
title: >-
  FavoriteNoItem and RatingNoItem are not in the ResponseStringsTests
  AllExpectedKeys ledger, so no locale walk guards them
status: Done
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

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

## Direction-word guard: decision record (worker, 2026-10-09)

SHAPE DECISION: the required-word-pair table in `scripts/validate_locales.py`
(`FAVORITE_NO_ITEM_DIRECTION_WORDS`), NOT handler-test pins. Rationale:

1. Ownership: the guarded artifact is the locale JSON VALUE. validate_locales
   already walks all 17 files and is the CI gate for locale content; the
   handler test guards handler behavior (key literals, en-US spoken
   substrings), and 16 non-English wordings are not handler behavior.
2. Drift frequency beats landing frequency (the sizing axis): a drifted
   translation lands far more often than a new locale, and the validator is
   the earliest and cheapest surface (no compile, no testhost; it also runs
   as its own CI job). NON-BLOCKING, stated plainly (review F1): the finding
   is warning-level, so the validate-locales CI job stays GREEN and only the
   job log carries the WARN block; the guard makes the drift visible in
   every run, it does not fail the build. Error-level was considered and
   rejected on the check-10 precedent: a legitimate direction-neutral
   rewording (it-IT "mettere o togliere") would break CI until the row
   updates.
3. Self-reminding completeness: a locale file present without a table row
   fires its own finding ("add one in the same change"), so the once-per-new-
   locale cost cannot be silently skipped; handler-test pins would need extra
   machinery to get the same property.
4. Severity follows the file's established value-content convention: empty
   values and extra keys are WARNINGS today; errors are reserved for
   structural/presence breaks (the runtime KeyNotFoundException class). The
   direction check is exactly the validate_interaction_models check-10 class
   (translation-content check where a LEGITIMATE rewording, e.g. it-IT
   "mettere o togliere", fires until the row updates), which is
   warning-level BY DESIGN so it can never break CI (the JF-556 note).
   Unlike known-gap warnings, direction findings print IN FULL in every mode
   (they have no baseline), so the firing is visible, not a count.

The 17 word pairs are sourced verbatim from the committed values (grep, not
invented): en-US/GB/AU/IN/CA add+remove; de-DE hinzufügen+entfernen;
es-ES/MX/US añadir+quitar; fr-FR/CA ajouter+retirer; it-IT
aggiungere+rimuovere; pt-BR adicionar+remover; nl-NL toevoegen+verwijderen;
ja-JP 追加+削除; hi-IN जोड़ा+हटाया; ar-SA إضافته+إزالته. A deliberate
rewording updates its row in the same change (the guard's own finding says
so).

PROOF (the validator has no pytest suite; the convention is the firing
demonstration + the clean baseline run):

- Clean baseline, guard live: PASS, exit 0, all 17 locales OK, zero
  direction findings, in both --check and --full (output byte-shape
  unchanged from the pre-guard run).
- Firing demo (scratch copy under /var/tmp, real locale files untouched):
  it-IT flipped add-only → "[it-IT] FavoriteNoItem lost direction wording:
  missing rimuovere (must name both add and remove)"; flipped remove-only →
  "missing aggiungere"; table row deleted → "[it-IT] FavoriteNoItem present
  but no direction-word row in FAVORITE_NO_ITEM_DIRECTION_WORDS (add one in
  the same change)". All three shapes fire; exit stays 0 (warning by
  design). Scratch deleted after; git status shows only
  scripts/validate_locales.py modified.

GATES (worker):

- /simplify (4 agents: reuse, simplification, efficiency, altitude):
  efficiency/simplification/altitude clean; the reuse finding (fold
  direction_findings into all_warnings per the validate_interaction_models
  inline-print precedent) REJECTED with reasons: this file's summary labels
  all_warnings as "known gap(s) / extra key(s)" so folding mislabels
  direction drift in --check counts, --full would double-print the strings
  (inline plus the first-10 listing), and one list per reporting class is
  this file's own convention (all_errors vs all_warnings).
- /code-review high (6 findings): F1 PARTIAL (the record now states the
  non-blocking semantics plainly, point 2 above; severity stays warning,
  reasons recorded). F2 APPLIED: matching is casefold + NFC on both sides,
  so a capitalization-only or Unicode-normalization-only rewording no longer
  fires falsely (reviewer-verified false positive "Aggiungere o Rimuovere").
  F3 APPLIED: ASCII direction words match with ASCII-letter boundaries
  (contains_direction_word) so 'add' inside 'address' can no longer satisfy
  the guard for the five en locales; non-ASCII scripts keep substring
  matching because \\b boundaries do not exist between CJK/Devanagari/Arabic
  characters. Known cost, accepted: inflected forms ('added') do not match
  and fire; a rewording that uses one updates its row in the same change
  (warning-level, self-documenting). F4 APPLIED: a locale carrying a
  direction finding now shows "direction wording" in its per-locale status
  row instead of a contradictory OK. F5 APPLIED: the module docstring names
  the value-content guard and its non-failing semantics. F6 APPLIED: a
  whitespace-only FavoriteNoItem reports only the empty-value warning (the
  direction check skips blank values), one finding per defect.
- Post-review re-verification: clean baseline PASS exit 0 in --check and
  --full with zero direction findings; firing demos re-run green-red (see
  commit message).
<!-- SECTION:NOTES:END -->

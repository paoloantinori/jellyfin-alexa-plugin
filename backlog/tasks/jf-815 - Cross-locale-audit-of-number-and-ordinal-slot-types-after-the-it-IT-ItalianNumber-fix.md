---
id: JF-815
title: >-
  JF-815 - Cross-locale audit of number and ordinal slot types after the it-IT
  ItalianNumber fix: verify every locale's numeric slots use AMAZON.NUMBER and
  the ordinal slots cover the vocabulary users actually speak
status: Done
assignee: []
created_date: '2026-10-08'
labels:
  - nlu
  - audit
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the 2026-10-08 live incident (Paolo's prompt after the it-IT fix):
the ItalianNumber custom type had silently made every episode and chapter above
15 unreachable by voice in it-IT for the skill's entire life. The question: is
the same class of defect present in other locales?

## The audit (executed 2026-10-08, mechanical, over all 17 committed models)

**Numeric slots (season_number / episode_number / chapter_number and any
*numero*/*number* slot): ALL 17 LOCALES CLEAN except the it-IT defect already
fixed.** 16 locales declare no custom number type at all and use AMAZON.NUMBER
for every numeric slot (ar-SA, de-DE, en-AU, en-CA, en-GB, en-IN, en-US, es-ES,
es-MX, es-US, fr-CA, fr-FR, hi-IN, ja-JP, nl-NL, pt-BR). it-IT was the sole
outlier; its PlayEpisode and GoToChapter slots switched to AMAZON.NUMBER in the
JF-814 part-1 fix (deployed and live-verified).

**Remaining custom numeric-family types in it-IT (both deliberate, both with
bounded vocabularies - no compound-word exposure):**
- RateItemIntent.star_rating -> ItalianNumber (values 1-15 + tens + 100): a
  rating is 1-5 by contract, the custom type constrains the elicit answers, and
  no legitimate user input exceeds the listed vocabulary.
- GoToChapterIntent.direction -> ItalianOrdinal (10 values, primo..decimo):
  direction is next/previous-class; verify the SAMPLES cover the words users
  say (see residual R2).

## Residuals

- R1 (none for numbers): no other locale carries the it-IT defect class.
- R2 (minor, it-IT only): the ItalianOrdinal direction slot's vocabulary
  coverage versus the samples ("prossimo/successivo/precedente" are plain words
  in samples, not ordinal type values - confirm no sample relies on an ordinal
  word beyond decimo; if users say "vai al prossimo capitolo" the direction
  slot shape must match).
- R3 (the JF-814 part-2 carry-over, tracked there): the season-less episode
  phrasing family in all 17 locales.

## Why this closes now

The audit is mechanical and complete over the committed models; the one defect
found was fixed same-day; the two remaining custom types have bounded
vocabularies with no compound-number exposure. The R2/R3 follow-ups are filed
where they belong (here as notes, JF-814 for the season-less family).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 All 17 committed models audited mechanically for custom numeric slot types (the audit table in the description)
- [x] #2 The one defect found (it-IT) fixed and live-verified (JF-814 part 1)
- [x] #3 The remaining custom types assessed for the same exposure (bounded vocabularies, no compounds)
- [x] #4 Residuals routed (R2 noted here, R3 in JF-814)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

R2 RESOLVED (2026-10-08, verified): the GoToChapter samples carry both shapes
("Vai al capitolo {chapter_number}" and "Vai al capitolo {direction}"); the
direction phrases users speak ("prossimo", "precedente", "successivo") arrive
as the DIRECTION slot where the samples accept free direction words - the
ItalianOrdinal type only gates ordinal-form directions (primo, secondo...), a
bounded vocabulary with no compound exposure. No change needed.
<!-- SECTION:NOTES:END -->

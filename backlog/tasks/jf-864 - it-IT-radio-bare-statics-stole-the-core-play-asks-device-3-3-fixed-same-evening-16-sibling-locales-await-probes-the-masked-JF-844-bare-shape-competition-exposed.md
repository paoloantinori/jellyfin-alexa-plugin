---
id: JF-864
title: >-
  it-IT radio bare statics stole the core play asks (device 3/3, fixed same
  evening); 16 sibling locales await probes; the masked JF-844 bare-shape
  competition exposed
status: Done
assignee: []
created_date: '2026-10-11 13:52'
updated_date: '2026-10-11 18:56'
labels:
  - nlu
  - interaction-model
  - regression
  - device-evidence
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-11 15:55 by the orchestrator from Paolo's device round (the first live test of the marathon: A1 failed 3/3 because 'riproduci inception' NEVER reached PlayVideo).

THE BUG (device-confirmed 3/3 at ~15:10, then profile-nlu reproduced): it-IT's PlayRadioIntent carried BARE static samples on a slot-required intent (anti-pattern #1): 'riproduci radio'/'suona radio'/'metti radio' taught the trainer the naked [imperative][X] pattern, which stole the core play asks - 'riproduci nemo', 'riproduci inception', 'metti nemo' ALL routed PlayRadioIntent with an empty station (the skill answered the radio elicit). 'riproduci il film nemo' routed correctly (the carrier disambiguates).

FIX LANDED (same evening, commit 64937beb + DLL 2949e0bd + it-IT rebuild SUCCEEDED): the three bare statics qualified with the article ('riproduci la radio' etc.), the bare 'riproduci simili' -> 'riproduci musica simile' (matching its siblings' noun-carried shapes); the working default (empty-station elicitation) UNCHANGED. POST-FIX PROBE MATRIX (3x each): the radio guards hold ('riproduci la radio'/'metti la radio'/'riproduci la stazione radio jazz' all PlayRadioIntent 3/3), the steal is dead (nemo/inception/metti-nemo no longer PlayRadio), 'riproduci il film nemo' still PlayVideoIntent.

RESIDUAL EXPOSED (recorded, NOT fixed tonight): the bare 'riproduci <movie>' shape now reads NO_SELECTION - the radio steal had been MASKING the pre-existing JF-844 competition (PlaySong steals 'riproduci breaking bad' via the series catalog; the naked SearchQuery title cannot win alone). That is the JF-844 maintainer-call territory (anti-pattern #11's coin-flip warning: bare carriers on free-text video slots vs PlaySong). DEVICE GUIDANCE: the carrier form ('riproduci il film X') works and always did.

OPEN ITEMS: (1) the 16 other locales carry the same bare [verb][radio] statics (surveyed: 'play radio'/'Spiele Radio'/'Reproduce radio'/... ; fr already article-qualified by accident) - probe each locale's crossing before changing (the JF-857 discipline; a bulk unprobed edit is template surgery without evidence); (2) the JF-844 bare-shape decision now has cleaner evidence to work from.
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PROBE MAP COMPLETE + nl-NL FIXED (2026-10-11 evening, orchestrator): the 16-locale crossing sweep ran live (bare play-ask + radio guard per locale). RESULTS: exactly ONE sibling had the radio steal - nl-NL ('speel nemo' -> PlayRadioIntent), fixed the same way ('speel de radio'/'speel de radiomodus'/'start de radio'), deployed (DLL rebuild + nl-NL rebuild SUCCEEDED) and verified live ('speel nemo' -> NO_SELECTION, 'speel de radio' -> PlayRadioIntent 3/3). The other 14: en-US 'play nemo'->PlaySong; de 'Spiele nemo'->PlaySong; es-US 'reproduce nemo'->PlaySong; fr-CA 'lis nemo'->PlaySong; en-AU 'play nemo'->PlayByGenre; es-ES 'reproduce nemo'->PlayByGenre; ja ->PlayArtistSongs; en-GB/es-MX/fr-FR/pt-BR/hi ->NO_SELECTION; ar-SA 'play nemo'->PlayVideo (CLEAN); radio guards hold everywhere except ar-SA's (TurnRadioOnIntent, Amazon's built-in winning its own utterance - different class, no action without harm evidence). Those are the JF-844-family landscape, untouched per probe-first. FIXTURE PINS landed same-evening (commit 420271a3): it-IT 'suona matrix' re-pinned from PlayRadio to the NO_SELECTION skip-record (the JF-585 convention) + the two qualified-form guards; nl-NL 'speel de radio' guard. Dry-run 8 passed/1175 skipped. REMAINING OPEN: only the JF-844 bare-shape product call (with the full 16-locale evidence table above).
<!-- SECTION:NOTES:END -->

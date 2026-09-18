---
id: TASK-HIGH.1
title: 'Annuncio episodio: data estesa al posto del doppio progressivo (it-IT)'
status: To Do
assignee: []
created_date: '2026-09-18 13:43'
labels: []
dependencies: []
parent_task_id: TASK-HIGH
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Da segnalazione utente (podcast quotidiani Il Post via IlPost plugin): l'annuncio legge un progressivo doppio. Il titolo episode.Name e' 'Ep. 1286 – La diserzione dei medici...' (nfo del sorgente) e la skill vi aggiunge/riconduce il numero di episodio: l'utente sente il progressivo due volte. Per un quotidiano la data e' l'informazione utile, non il progressivo.

Proposta (livello annuncio, zero tocchi al sorgente Il Post e zero rinomine file): quando l'item e' un Episode con PremiereDate e il locale e' it-IT, comporre l'annuncio come '<giorno-settimana esteso> <giorno> <mese> – <titolo senza prefisso Ep. N>' (es. 'Mercoledì 17 settembre – La diserzione dei medici e le altre storie di oggi'), usando DateTime.ToString con cultura it-IT. Il titolo mostrato su APL/display resta completo. En e altri locali: comportamento attuale invariato (nome item).

Punti toccati: SpeechBuilder.BuildNowPlayingSpeech / PlayingNextEpisodeSsml / PlayingLatestEpisodeSsml (TvNextUpService.cs:414) e il ramo announce di PlaybackLaunchBuilder. ItalianNumberWords resta per gli altri usi. Attenzione: niente rinomine file, niente tocchi al nfo (il sorgente e' il plugin IlPost, fuori repo).

Verifica: test SpeechBuilder con Episode item (con/senza PremiereDate, con/senza prefisso Ep.), e su device reale con un podcast Il Post.
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

## Implementation Notes (2026-09-18)
<!-- SECTION:IMPLEMENTATION_NOTES:BEGIN -->
- Helper: `SpeechBuilder.FormatEpisodeAnnounceTitle(BaseItem item, string locale)` (Alexa/Util/SpeechBuilder.cs). Returns the date-formatted title ONLY when item is an Episode, PremiereDate has a value, and locale is it-IT (OrdinalIgnoreCase); else null. Format: `PremiereDate.ToString("dddd d MMMM", CultureInfo("it-IT"))`, first letter capitalized, separator "–" (en-dash, from the task spec), title = item.Name minus a leading `Ep. N – `/`Ep. N - ` prefix (regex `^\s*ep\.?\s*\d+\s*[-–]\s*`, en-dash and hyphen, case/space tolerant); a name without the prefix gets the date prepended to the full name.
- Call sites adopted (both cover the VideoApp and the JF-586/JF-589 audio-degrade routes because the speech is caller-composed):
  - `PlaybackLaunchBuilder.BuildVideoLaunchSpeech` (ticks overload; the deps overload delegates here): covers PlayVideo, StartOver, ResumeIntent fallback-4, ContinueWatching/SearchMedia, AplUserEvent, TvNextUpService resume arm. Both the resume and fresh-play arms use the formatted title.
  - `TvNextUpService` fresh-play arm (PlayingNextEpisode/PlayingLatestEpisode keys, ~line 414).
- Display/APL metadata untouched (BuildVideoAppLaunchResponseAsync metadata, cards keep item.Name). No interaction-model changes; no other locales affected.
- Tests (red-first: CS0117 before the helper existed): new `Jellyfin.Plugin.AlexaSkill.Tests/Unit/SpeechBuilderEpisodeAnnounceTests.cs`, 9 tests - formatted + Ep-prefix strip (en-dash and hyphen), missing PremiereDate null, non-Episode null, en-US null, no-prefix date-prepend, it-IT culture correctness (Mercoledì, not Wednesday), and two builder-level tests (fresh-play arm, resume arm).
- Verification: full `dotnet test` both TFMs green (4083/4083 each, includes the new 9), `dotnet build -c Release` 0 warnings. Uncommitted; left for the orchestrator's gates.
- Left for the user: on-device check with a real Il Post podcast episode (voice announce reads "Mercoledì 17 settembre - ..." and the APL title stays "Ep. 1286 - ...").
<!-- SECTION:IMPLEMENTATION_NOTES:END -->
<!-- DOD:END -->

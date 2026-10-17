# Research Report: Perché simulate-skill fallisce su fr-FR/es-ES da giorni (il "quasi una settimana")

**Date**: 2026-10-17
**Depth**: exhaustive
**Confidence**: HIGH sul meccanismo (probe propri, controllati, ripetuti); MEDIUM sul "perché fr è peggio"

## Executive Summary

Non è un outage, non sono endpoint cambiati, non siamo bannati e non è un nostro bug di pipeline, MA la ricerca ha trovato un mio errore metodologico reale nel report del 2026-10-15. La vera condizione: il servizio simulate è attivo e le nostre chiamate funzionano; ciò che fallisce è la SELEZIONE DELLA SKILL da parte del router Alexa per il dispositivo simulato, ed è INTERMITTENTE PER CHIAMATA: la stessa coppia (locale, frase) oggi è passata da FAILED con `<IntentForDifferentSkill>` a SUCCESSFUL nel giro di quindici minuti, con lo stesso token e la stessa pipeline. fr-FR/fr-CA sono le peggiori (falliscono da giorni nella batteria), de-DE ha mostrato il primo fallimento oggi, es-ES alterna, it-IT ed en-GB restano stabili. Verdetto per le cinque ipotesi di Paolo: (1) endpoint cambiati NO, (2) ban NO, (3) servizio rimosso NO, il router invece è instabile SI, (4) "sbagli qualcosa tu" SI ma non dove pensavamo: il confronto decisivo del report del 15 ottobre confrontava una frase fr nuda senza sessione contro una frase it con invocazione, un confronto truccato che ha prodotto il falso "fr giù, it su" (dettagli sotto).

## Findings

### 1. La batteria di probe di stamattina (2026-10-17, 09:30-10:15, CLI ask + REST raw, stesso token, stage development)

| Probe | Forma | Esito |
|---|---|---|
| it-IT "apri mia collezione" | open (CLI) | SUCCESS, LaunchRequest verso di noi |
| it-IT "metti una canzone dei pink floyd" | nuda in sessione (CLI) | SUCCESS, PlayArtistSongsIntent |
| it-IT "chiedi a mia collezione di suonare i radiohead" | one-shot con invocazione (raw FORCE_NEW) | SUCCESS, ER completa |
| en-GB "open jellyfin player" | open (CLI) | SUCCESS, LaunchRequest |
| es-ES "abre mi colección" | open (CLI) | SUCCESS, IntentRequest (i nostri intent nei considered) |
| es-ES "pon las canciones de pink floyd" | nuda, stato sessione residuo (CLI) | SUCCESS |
| fr-FR "ouvre mon serveur" | open (raw FORCE_NEW, raw DEFAULT, CLI) | FAILED x3, `<IntentForDifferentSkill>` |
| fr-FR "ouvre mon serveur" | open (CLI, ~15 min dopo) | SUCCESS x2 |
| fr-FR "demande à mon serveur de lire les chansons de pink floyd" | one-shot con invocazione (raw + CLI) | SUCCESS x2 |
| fr-FR/fr-CA/de-DE/it-IT/fr nude | FORCE_NEW senza sessione | FAILED per TUTTE le locale (vedi 3) |
| de-DE "öffne meine sammlung" | open (CLI) | FAILED 1 volta oggi, `<IntentForDifferentSkill>` |

Codici HTTP: 200 ovunque, mai 401/403/429. Le simulazioni riuscite si alternano a quelle fallite negli stessi minuti, stesso account.

### 2. Stato dei model: tutte le 17 locale SUCCEEDED

get-skill-status: quick build, full build, dialog model, name-free interaction: SUCCEEDED per ognuna delle 17 locale. I nomi d'invocazione live corrispondono ESATTAMENTE ai fixture (fr "mon serveur", es "mi colección", de "meine sammlung", it "mia collezione", en "jellyfin player"). Nessun drift nostro, nessun build fallito: l'ipotesi 5 è confutata.

### 3. Il mio errore nel report del 2026-10-15 (correzione dovuta)

Il probe "decisivo" di quel report confrontava fr-FR "lis les chansons de pink floyd" (frase NUDA, FORCE_NEW_SESSION, senza invocazione) contro it-IT "chiedi a mia collezione di..." (frase CON invocazione). Due differenze confondevano locale e forma. Stamattina il controllo corretto mostra: la frase nuda senza sessione fallisce PER OGNI LOCALE, anche it-IT e de-DE ("metti una canzone dei pink floyd" FORCE_NEW it-IT: FAILED, `<IntentForDifferentSkill>`), perché senza sessione e senza invocazione il router sceglie un'altra skill: è la semantica attesa, non un guasto. La batteria e2e funziona in two-step (open con invocazione, poi la nuda a cavallo della sessione): la conclusione "degrado Amazon-side fr/es" del 15 restava vera nel fenomeno (le righe fr/es fallivano davvero) ma la prova portata era sbagliata e la classe era mal classificata: non "servizio simulate degradato per quelle locale" bensì "router di selezione skill instabile" (punto 4).

### 4. Il meccanismo reale: selezione skill instabile nel dispositivo simulato

La firma di ogni fallimento è `consideredIntents: [<IntentForDifferentSkill>...]` con messaggio "did not resolve to any intent in your skill": il NLU gira, considera, e sceglie UN'ALTRA skill. Oggi "ouvre mon serveur" ha fallito tre volte e poi passato due volte, identico in tutto. La classe è documentata come ricorrente per le locale non it/en (memoria simulate_outage_per_locale) e ha un precedente storico in Stack Overflow 59058251 (2019, stessa firma, risolta lato selezione). Fattore our-side-adjacent plausibile ma NON verificato: i nostri nomi d'invocazione non-it sono frasi generiche ("mon serveur" in francese è "il mio server") che competono con più skill nei rispettivi marketplace; it ("mia collezione") ed en ("jellyfin player") competono meno.

### 5. Wart collaterale notato (famiglia diversa, qualità NLU non routing)

Il one-shot fr "demande à mon serveur de lire les chansons de pink floyd" instrada su QueryArtistLibraryIntent invece del play: scelta d'intent sbagliata ma DENTRO la nostra skill; famiglia model-quality, non c'entra la selezione.

## Confidence Assessment

- Meccanismo (router intermittente, non servizio giù): HIGH, prove dirette ripetute con controlli.
- Build/modelli sani e invocation names allineate: HIGH (fonti Amazon live).
- Nessun ban/throttle: HIGH (200 ovunque, successi e falliti negli stessi minuti).
- "Perché fr peggio delle altre": MEDIUM-LOW, ipotesi della genericità del nome non testata.
- Corroboration esterna della ricorrenza: MEDIUM (un precedente SO 2019; i forum attuali non offrono riscontri puliti, le ricerche di oggi hanno reso soprattutto rumore).

## Sources

1. Probe propri 2026-10-17 (CLI ask smapi simulate-skill / get-skill-simulation + REST raw su api.amazonalexa.com con token LWA della macchina; tabella al punto 1).
2. ask smapi get-skill-status (build stato per 17 locale) e get-interaction-model (invocationName per locale), 2026-10-17.
3. https://stackoverflow.com/questions/59058251/ask-cli-simulate-not-resolving-skill-returning-intentfordifferentskill-error (precedente storico della firma, 2019).
4. Report interno 2026-10-15 claudedocs/research_smapi-endpoints_2026-10-15.md (il cui probe decisivo è corretto dal punto 3 di questo report).
5. Skill Simulation REST API Reference (developer.amazon.com), struttura risposta confermata in vivo.

## Follow-up suggeriti (nessuno eseguito: report-only)

1. Correggere il report del 2026-10-15 (appendere la correzione del probe decisivo).
2. Task: estendere il fallback JF-656 della batteria smoke alla classe open-failed-per-IntentForDifferentSkill (oggi scatta solo per SmapiUnexpectedError): un open che perde il router cade nel one-shot prefissato invece di arrossare la riga.
3. Task opzionale: valutare un secondo open-retry sulle locale fragili prima del fallback.
4. Separato: la scelta QueryArtistLibraryIntent sul one-shot fr play (punto 5) merita un'indagine model-side se si ripresenta.

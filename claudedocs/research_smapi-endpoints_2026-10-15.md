# Research Report: SMAPI Simulation/Profiler endpoint changes (the fr/es outage question)

**Date**: 2026-10-15
**Depth**: exhaustive
**Confidence**: HIGH (official doc scraped live + raw REST probe against the production endpoint, control included)

> **CORRECTION 2026-10-17** (see claudedocs/research_simulate-routing_2026-10-17.md): finding 4's
> "decisive" probe compared fr-FR with a BARE phrase under FORCE_NEW_SESSION against an it-IT
> control carrying the INVOCATION. That shape pair is not a fair comparison: the bare,
> session-less form fails for EVERY locale (it-IT and de-DE included), because without a session
> and without an invocation the router selects another skill; it is expected semantics, not an
> outage. The endpoint conclusions (finding 1) and the response-schema work (finding 2) stand.
> The phenomenon itself (the e2e fr/es rows failing) was real, but the correct classification is
> intermittent skill-SELECTION flakiness in the simulated device's router, not a simulate-service
> degradation scoped to those locales: on 2026-10-17 the same fr open utterance failed three
> times and succeeded twice within fifteen minutes on the identical pipeline, and a fr one-shot
> with the invocation routed to the skill every time.

## Executive Summary

Gli endpoint NON sono cambiati. La superficie REST della Skill Simulation API è la solita v2 documentata (`POST /v2/skills/{id}/stages/{stage}/simulations` + `GET .../simulations/{simulationId}` su `api.amazonalexa.com`), e la Utterance Profiler API resta `POST /v1/skills/{id}/stages/{stage}/interactionModel/locales/{locale}/profileNlu`. Il degrado fr/es è confermato Amazon-side DA UNA CHIAMATA REST DIRETTA (bypassando completamente l'ask CLI): fr-FR risponde "did not resolve to any intent" mentre it-IT, con lo stesso codice e lo stesso token, invoca `PlayArtistSongsIntent`. Non è un problema nostro né del CLI.

## Findings

### 1. Endpoint REST: invariati (fonte: doc ufficiale live, scraped 2026-10-15)

- Simulation API: `https://api.amazonalexa.com`, operazioni `POST /v2/skills/{skill-id}/stages/{stage}/simulations` e `GET /v2/skills/{skill-id}/stages/{stage}/simulations/{simulationId}` [1]. Esattamente quelle che usiamo.
- Utterance Profiler API: stesso host, `POST /v1/skills/{skillId}/stages/{stage}/interactionModel/locales/{locale}/profileNlu` [2]. Invariata.
- Nessuna v3 o endpoint nuovo trovata in nessuna fonte.

### 2. Lo schema di risposta È cambiato (già scoperto e corretto da noi il 2026-10-14)

La doc ufficiale attuale documenta `result.skillExecutionInfo.invocations[]` con `invocationRequest.body` / `invocationResponse.body` [1]: la forma array che il fix della batteria ha adottato è quella documentata. La vecchia forma piatta `skillInvocationInfo` non è più nella doc. Nota dal probe raw: `invocationRequest.body` può arrivare già come dict (non stringa JSON) nella risposta REST; il parsing deve essere dict-aware.

### 3. Il CLI ask: `--locale` rimosso da simulate-skill (empirico, 2026-10-14)

ask CLI 2.30.7 (installato = ultimo pubblicato su npm): `ask smapi simulate-skill` rifiuta `--locale` ("unknown option"); resta `--device-locale`. Il `get-skill-simulation` funziona. Questo era il falso-outage della batteria, già corretto.

### 4. Il degrado fr/es è Amazon-side: PROVA DECISIVA via REST raw (2026-10-15)

Chiamata REST diretta con il token LWA valido (scavato dalla config del CLI, expiry verificata):
- **fr-FR** "lis les chansons de pink floyd": POST 200 IN_PROGRESS, poi il poll risponde FAILED con "This utterance did not resolve to any intent in your skill" [probe b61f4853].
- **it-IT** "chiedi a mia collezione di suonare i radiohead" (controllo, stesso codice/token/pipeline): SUCCESSFUL, intent invocato `PlayArtistSongsIntent`.
- I modelli salvati fr-IT/es-ES risolvono le frasi correttamente su profile-nlu (verificato 2026-10-13: PlayArtistSongsIntent 3/3): il modello è sano, è il runtime di simulazione NLU per quelle locale che non risolve.
- Conclusione: non è l'endpoint, non è il CLI, non è il nostro modello. È il servizio di simulazione Amazon per quelle locale (classe ricorrente già documentata in `simulate_outage_per_locale`).

### 5. Nota minore: la lista locale nella doc è stantia

La doc elenca per `device.locale` solo 8 valori (en-US/GB/CA/AU, de-DE, fr-FR, en-IN, ja-JP) [1] ma it-IT funziona e non è elencata: la lista nella doc non è esaustiva/aggiornata, non è un vincolo reale.

## Confidence Assessment

- Endpoint invariati: HIGH (doc ufficiale live + probe raw funzionante).
- Schema risposta a invocations[]: HIGH (doc + nostra correzione empirica del 2026-10-14).
- Degrado fr/es Amazon-side: HIGH (probe raw con controllo it-IT nello stesso momento).
- Lista locale doc stantia: MEDIUM (inferenza dal funzionamento di it-IT).

## Sources

1. https://developer.amazon.com/en-US/docs/alexa/smapi/skill-simulation-api.html (Skill Simulation REST API Reference, scraped live 2026-10-15)
2. https://developer.amazon.com/en-US/docs/alexa/smapi/utterance-profiler-api.html (Utterance Profiler REST API, endpoint section, scraped live 2026-10-15)
3. Probe raw REST (token LWA della macchina, SID 33dfacd5): fr-FR FAILED / it-IT SUCCESSFUL PlayArtistSongsIntent, 2026-10-15 ~21:40
4. ask CLI 2.30.7 (`ask --version` = `npm view ask-cli version`), comportamento `--locale` empirico 2026-10-14

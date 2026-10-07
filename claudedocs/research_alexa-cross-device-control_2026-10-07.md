# Research Report: Cross-device control per skill Alexa custom

**Date**: 2026-10-07
**Depth**: exhaustive (3 agenti paralleli, 20+ fonti, verifica avversariale su meccanismo e casi reali)
**Confidence**: HIGH sul muro dei directive e sulle meccaniche; MEDIUM sui comportamenti non documentati (transfer nativo su stream custom, remote-stop via stream kill)

## Executive Summary

Il muro che abbiamo documentato è confermato costruttivamente: lo schema di risposta Alexa non ha alcun campo di indirizzamento (docs aggiornate ago 2026), quindi una skill custom può inviare directive solo al dispositivo che ha fatto la richiesta. Il comando nativo "sposta la riproduzione" esiste ed è il directive `Alexa.Media.Playback.Reinitiate` del Music/Radio/Podcast Skill API (con playbackSnapshot di coda, offset, origine e destinazioni), ma le skill AudioPlayer custom non lo ricevono mai. L'osservazione di Paolo (alcune skill agiscono su altri dispositivi) è VERA e si spiega in tre classi: le skill smart-home dirigono endpoint ovunque (ma un Echo non è un endpoint controllabile da terzi), le skill media-server (LMS MediaServer) sono autoritative sul PROPRIO server (l'Echo che streama è solo un player lato server, quindi il transfer è server-su-server), e il percorso non ufficiale a cookie (alexapy). La conseguenza per noi: il nostro archivio è già server-autoritativo sui percorsi che controlliamo (gli endpoint HLS tokenati), quindi il remote-stop pulito è costruibile dove serviamo noi lo stream, e il pull-side esiste già (FollowMe).

## Findings

### 1. Il muro dei directive: confermato (HIGH)
Lo schema Request/Response (aggiornato 17 ago 2026) non ha campo device/endpoint/addressing: i directives sono per il dispositivo della richiesta; `AudioPlayer.Play` ha solo playBehavior + audioItem. L'unica superficie cross-device mai spedita (notifiche Alexa Smart Properties) è riservata ai vendor hospitality. Fonti: request-and-response-json-reference, audioplayer-interface-reference (developer.amazon.com), SO 75040849.

### 2. Il transfer nativo è il Reinitiate del Music Skill API (HIGH sulle meccaniche, MEDIUM sul caso custom)
"Alexa, move my music to X" → Alexa invia alla skill `Alexa.Media.Playback.Reinitiate` con playbackSnapshot (queue id, offset, endpoint origine, endpoint destinazione); la skill risponde CONFIRM_QUEUE o REPLACE_STREAM e Alexa riprende a quell'offset sul target (docs alexa-media-playback, aggiornato dic 2023). Le skill custom AudioPlayer NON ricevono mai questo directive; per loro l'unica richiesta osservabile durante un transfer è il normale `PlaybackStopped` sull'ORIGINE (i docs lo descrivono per gli stop da richiesta vocale: inferenza etichettata, nessuna fonte lo documenta per i transfer). Prova del consumatore: il transfer nativo funziona con Amazon Music e Spotify (Spotify via il proprio Connect), con riporti di fallimento fuori da quelle sorgenti (CNET nov 2021, Gadgets360 nov 2021, SmarthomePoint 2023).

### 3. Come lo fanno i terzi oggi (HIGH, il caso che interessa)
- **LMS MediaServer (smartskills.tech)**: il follow-me NON è Alexa-side. Il servizio cloud della skill pilota l'API del server LMS attraverso un tunnel https; l'Echo che streama `/stream.mp3` appare in LMS come player effimero "AlexaPlayer", quindi il transfer è sempre player-LMS → player-LMS, compreso l'Echo streaming. Lo stop della sorgente è server-side (LMS smette di alimentare lo stream e spegne il player con playlist svuotata); il target continua alla stessa posizione temporale. Il targeting è uno slot (AMAZON.Room) e ogni Echo può "assumere" un player.
- **Bock Media**: "Play on device" spinge la playlist su un Echo scelto via API non ufficiale a cookie (alexapy, quella di alexa_media_player): fragile, zona grigia, non ASK.
- **Smart-home**: un Echo NON è un endpoint controllabile da terzi; LMS-lite registra i PLAYER LMS come dispositivi smart-home usabili nelle routine, non gli Echo. Le routine utente possono fermare l'audio su un Echo nativamente, ma le skill non possono creare né innescare routine (tranne: vedi 5).
- **REMOTE STOP di uno stream custom via ASK: NOT_FOUND** in tutta la ricerca; nessuna skill documenta la tecnica.

### 4. Il nostro leverage: morte dello stream lato server (MEDIUM-HIGH sulle meccaniche, NON provato su device)
- EOF pulito lato server = `PlaybackFinished` ("Notifies your skill when the stream comes to an end on its own", docs AudioPlayer): lo stop pulito alla icecast è documentato (HIGH).
- Kill brusco (RST/404) a metà stream: ExoPlayer riprova un numero limitato di volte (minLoadableRetryCount 3 per progressivi, 6 per live-adaptive; ParserException/FileNotFound esclusi dal retry) e poi il playback TERMINA con errore (PlaybackFailed); niente retry infinito, niente stallo eterno (blog ExoPlayer 2018, reference media3 corrente; il firmware Echo è un fork Amazon: inferenza MEDIUM). Il device resta fermo "in attesa di direttive" e la skill può rispondere con qualsiasi AudioPlayer directive.
- Nessun account pubblico di una skill che la usi deliberatamente come remote-stop: trattarla come NON provata su device fino a un probe.

### 5. L'attuatore a livello account (HIGH sull'esistenza, PREVIEW sull'accesso)
- **Custom Triggers for Routines** (docs aggiornati ott 2025): la skill definisce trigger e POSTa istanze via endpoint REST unicast/multicast, e "Alexa esegue le routine per il cliente target" - una routine con azione "Stop Audio" su un Echo scelto è l'attuatore. MA è developer preview dietro contatto a alexa-custom-triggers-support@amazon.com.
- **Voice Monkey pattern** (self-serve): una skill smart-home separata con sensore virtuale il cui ChangeReport innesca la routine. Richiede una SECONDA skill, non la custom.
- Proactive Events non innesca routine (solo notifiche).

## Cosa è costruibile per noi, in ordine

1. **Oggi, senza codice**: "seguimi" sull'Echo di destinazione (già implementato, JF-130) + lo stop manuale sulla sorgente. La catena nativa "sposta → PlaybackStopped sull'origine → seguimi sul target" funziona perché il nostro FollowMe tira la coda e il tracker porta la posizione.
2. **Costruibile sui percorsi nostri** (libri e video-audio HLS tokenati): remote-stop server-side via EOF/terminazione dello stream della sorgente quando l'utente fa follow-me, così il transfer diventa completo senza toccare il primo Echo. Serve il device probe (JF-516-family): cosa fa realmente l'Echo Show alla morte pulita vs brusca del nostro HLS.
3. **Da indagare per la musica semplice**: lo stream parte da Jellyfin direttamente; verificare se la fetch dell'Echo registra una sessione Jellyfin fermabile via /Sessions API (il plugin già guarda le sessioni).
4. **Non costruibile**: ricevere il Reinitiate (MSAPI only), indirizzare directive ad altro Echo, innescare routine nativamente (preview partner).

## Confidence Assessment
- HIGH: assenza del campo di indirizzamento; Reinitiate = MSAPI only; il meccanismo LMS server-side; PlaybackFinished su EOF; ExoPlayer retry limitati; Custom Triggers esiste in preview.
- MEDIUM: il comportamento del transfer nativo su stream custom (non documentato, inferenza strutturale da due set di docs); la morte dello stream come remote-stop (meccanica solida, nessun precedenti pubblico); l'irrorazione alexapy come terza classe.
- NON verificato: il firmware Echo reale (fork Amazon), la registrazione di sessione Jellyfin per le fetch dirette.

## Sources
1. https://developer.amazon.com/en-US/docs/alexa/custom-skills/request-and-response-json-reference.html (ago 2026)
2. https://developer.amazon.com/en-GB/docs/alexa/custom-skills/audioplayer-interface-reference.html (set 2024)
3. https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-media-playback.html (dic 2023)
4. https://developer.amazon.com/en-US/docs/alexa/smapi/proactive-events-api.html (apr 2025)
5. https://developer.amazon.com/en-US/docs/alexa/ask-overviews/deprecated-features.html (ago 2026)
6. https://developer.amazon.com/en-US/docs/alexa/routines/introduction-to-custom-trigger-for-routines.html (ott 2025)
7. https://mediaserver.smartskills.tech/commandref/FollowMe.html + /Transfer.html + /mediaserverhelp.html
8. https://lyrion.org/extensions/applications/
9. https://github.com/andystumpf/bock-media + community.home-assistant.io/t/1013305
10. https://www.smarthomepoint.com/echo-music-transfer-another-echo/ (2023)
11. https://www.cnet.com/home/smart-home/how-to-move-music-with-alexa-on-amazon-echo-devices/ (nov 2021)
12. https://medium.com/google-exoplayer/load-error-handling-in-exoplayer-488ab6908135 (2018)
13. https://developer.android.com/reference/kotlin/androidx/media3/exoplayer/upstream/DefaultLoadErrorHandlingPolicy
14. https://www.amazonforum.com/s/question/0D56Q0000AbHnE5SQK/ (gruppi Echo)
15. https://voicemonkey.io/integrations/home-assistant + https://www.amazon.com/Virtual-Smart-Home-Routine-Trigger/dp/B08FCKD62D
16. https://stackoverflow.com/questions/75040849/ (gen 2023)

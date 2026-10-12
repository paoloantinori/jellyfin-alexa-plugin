# Checkpoint mattino 2026-10-13 (notte 12-13/10)

## Cosa è atterrato e DEPLOYATO su minix (tutto verificato)

1. **JF-870**: "Ne ho {0} in più." al posto di "E {0} in più." nel disambiguamento; fermare un libro dice "Ho fermato il libro." (17 locale).
2. **JF-871**: un film col titolo esatto viene proposto PRIMA del "intendevi Sunny?"; il "no" a un suggerimento fa girare tutta la cascata (artista, album, video, not-found onesto).
3. **JF-869**: one-shot libri ("chiedi a mia collezione di leggere il libro option b") ok; famiglia salta in italiano; serie ancorata; fix regressione PlayEpisode (righe bare-infinitive serie-first).
4. **JF-874**: COMANDI ONE-SHOT PER I FILM: "chiedi a mia collezione di mettere/riprodurre/vedere/guardare il film X" instradano su PlayMovieIntent con il titolo ER-matched (10/10 probe live). Catalogo MovieTitle cablato (fifth type), sync forzata col backdate documentato.
5. **JF-875**: rewind en-US che andava avanti corretto (rewind ora ER 'back'); famiglia salta in 15 locale (12 deployate; es famiglia e ja NEVER_BUILT, locale inattive: classe nota). Un'unica forma pt-BR furta da PlaySong su questo trainer roll (varianza documentata).
6. **Fix APL slideshow (JF-329 item 2)**: il SetPage spedito era MORTO (pager fermo a foto 1: 4 cause verificate sul runtime Amazon); la forma provata (sequencer + relative + 1) è deployata. Derisk fatto col Code Sandbox di Amazon senza login.
7. **JF-848**: i seed statici sono RISERVATI alla saturazione del cap (la libreria satura non cancella più il pavimento di recall). Attivo sul DLL deployato.
8. **CI**: verde su main (ultimo run sul push 0eb0f2cb).

## Da testare tu col device (round di oggi, frasi già verificate)

- **Pausa video**: "chiedi a mia collezione di mettere il film sunny dancer" (ora ONE-SHOT!) → "Alexa, pausa" → atteso "video interrotto" + video fermo.
- **Slideshow foto**: "chiedi a mia collezione mostra le foto di <album>" → atteso: le foto ora AVANZANO ogni 8s (prima restavano a foto 1, causa trovata e corretta). Da verificare anche la persistenza oltre 30s.
- **Salta musica**: "Alexa, chiedi a mia collezione di saltare avanti di trenta secondi" e il nudo "Alexa, salta avanti".
- **Libro one-shot**: "chiedi a mia collezione di leggere il libro option b".
- **Regressione**: "chiedi a mia collezione di mettere il brano sunny dancer" → deve restare la CANZONE (su live ora è verde; se diventa il film, è JF-876).

## Decisioni che ti aspettano (gate:maintainer-call)

- **JF-872**: il follow-up aperto dopo la pausa musica serve davvero? (tu hai osservato che la ripresa funziona anche dopo la chiusura dell'ascolto)
- **JF-879**: semantica della banda sotto-cap nel catalogo (probabilità remota, libreria al 2.3% del cap)
- **JF-876**: furto brano/film su "metti il brano sunny dancer" (NON riprodotto live; osserva e decidi fix vs accetto)
- Wording trailer disambiguamento: "Ne ho {0} in più." va bene o preferisci variante con "artisti" (sbaglia l'accordo con 1)?

## Aperti ma non bloccanti

- e2e retry dei 3 falliti (outage simulate attivo tutta la notte; riprovo al prossimo giro pulito)
- CI in corso sul push finale dei record

## Task nuovi creati stanotte

JF-869/870/871/872 (device round), JF-874/875/848 (eseguiti), JF-876/877/878/879 (residui).

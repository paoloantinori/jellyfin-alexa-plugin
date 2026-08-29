# Manual on-device verification checklist (2026-08-29 session)

Everything below is already live on the dev skill (models pushed, DLL deployed to minix).
For each item: say the phrase, note what you heard. Expected outcomes are what the
simulator and profile-nlu verified; the device adds real ASR, which only you can check.
If something fails, note the approximate TIME and what you said: the logs are classified
from the timestamp in seconds.

## A. The Koop flow (JF-408/411/412 fallout)

1. "Alexa, chiedi a mia collezione un disco dei Koop"
   - BEST: music starts, track 1 of Waltz for Koop, no speech (silent launch).
   - FALLBACK (acceptable): "Di quale artista vuoi ascoltare un album?" -> answer "koop"
     -> music starts.
2. Same phrase, but say "Koop" quickly/carelessly (ASR stress test).
   - Expect either the music, or the artist question (never "quale album", never
     recent-content lists, never a wrong artist like Porcupine Tree).
3. "Alexa, chiedi a mia collezione un disco" (no artist at all)
   - Expect: "Di quale artista vuoi ascoltare un album?" -> answer an artist -> album plays.
4. While that question is open, say "quali ci sono"
   - Expect: stays in the flow (e.g. artist not-found wording). NOT a list of recent albums.

## B. Cancel words during open questions (JF-413 review batch)

5. Get any skill question open (album elicit, FindSong "che canzone...", disambiguation),
   then say "ferma" or "stop"
   - Expect: "Ok, ho interrotto la ricerca." and silence (session ends).
6. "Alexa, chiedi a mia collezione di trovare la canzone stop" (a song titled like a
   cancel word)
   - Expect: a normal search flow (probably not-found), NOT the cancel message.

## C. PlaySong elicit (JF-413)

7. "Alexa, chiedi a mia collezione di mettere una canzone" (no title)
   - Expect: "Quale canzone vuoi ascoltare?" -> say a title you own -> it plays.
   - Also try with an artist included: "mettere una canzone di Koop" -> question ->
     title -> plays Koop's track (the musician must survive the round-trip).

## D. Stop decomposition (JF-392)

8. During playback: "Alexa, stop" (platform behavior, informational)
   - Note what happens. "pausa" is the reliable one; one-shot "chiedi a mia collezione
     ferma" the other. This just confirms the classification, no fix expected.

## E. Multilingual album-by-artist (JF-414; only if you switch device locale)

9. en: "ask jellyfin player for an album by coldplay" - routing works, but the Musician
   slot may return a knowledge-graph canonical ("Christopher Anthony John Martin") and
   you will hear a not-found. This is the documented en-* platform finding (JF-414
   residual): note whether it happens on the real device too.
10. de/fr (if testable): "ein Album von Queen" / "un album de Queen" -> should play.

## F. Duplicate-track regression (JF-409)

11. Play any album with PreEnqueueOnStart on, let 2-3 tracks pass.
    - Expect: no track plays twice in a row anymore.

## G. PlaybackStarted stalls (JF-410)

12. Play music for a while. If you EVER hear "Qualcosa è andato storto" again, note the
    time: the new telemetry will show whether the report was slow but the response fast
    (fix working) or both slow (platform regression).

## After the session

Tell me the time-stamped results (even just "1 ok, 2 fallback, 5 ok..."). I will pull
the logs for each timestamp and classify any failure into the documented buckets
(ASR capture, platform competition, plugin regression).

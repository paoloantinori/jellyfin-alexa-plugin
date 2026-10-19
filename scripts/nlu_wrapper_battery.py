#!/usr/bin/env python3
"""Wrapper-phrase NLU battery (2026-09-22, Paolo's directive after the fourth
wrapper-form gap in two days).

Two verification modes, because profile-nlu and simulate-skill see different
layers (verified live 2026-09-22):

- "profile" rows carry NO invocation prefix. profile-nlu matches the raw text
  against the saved model, which is exactly right for these (bare infinitives,
  imperative forms, connector-residue payloads like "que reproduzca ...").
  REFINED (JF-877, 2026-10-20): a prefixed phrase is NOT auto-fail either way -
  profile-nlu routes it ONLY when an explicit wrapper-tail row anchors the tail
  (the "de sauter"/"que salte"/"di saltare" twin class, live-verified fr
  acceptance 2026-10-20); the fixture suite pins those composed forms as
  profile rows deliberately. When adding a row, prefer the bare payload here
  and pin the composed form in the fixtures.
- "simulate" rows carry the full one-shot wrapper ("chiedi a mia collezione
  di ..."). Only the simulate pipeline models the invocation-matching layer
  that strips the prefix. These rows run a LOCALE evergreen control first
  (EVERGREEN_CONTROLS); when the control itself fails to invoke, the
  per-locale simulate outage (memory: simulate_outage_per_locale; recurring
  09-13/14/21-22) is active and the wrapper rows SKIP with an OUTAGE marker
  (not FAIL). A locale with simulate rows but no known evergreen control is a
  configuration error (exit 2): es/nl one-shot wrappers are
  invocation-layer-unreliable (SmapiClient.INVOCATION_PREFIX notes), so those
  sections use profile rows instead.

Any real FAIL means a template-twin bug: add the infinitive/wrapper twin to the
locale's interaction-model template (JF-551 wrapper families, CLAUDE.md
anti-patterns #1/#3/#11), regenerate, redeploy, re-run. Never hand-edit the
generated JSON, never weaken an expectation.

Usage:
  python3 scripts/nlu_wrapper_battery.py                # it-IT (the live household)
  python3 scripts/nlu_wrapper_battery.py --skill-id amzn1.ask.skill....
  SMAPI_DELAY=3 python3 ...                             # space the SMAPI calls

Requires the ask CLI. Exit 0 = all ran rows passed (or skipped on outage);
exit 1 = at least one real failure; exit 2 = cannot run (no skill, no battery).
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

# (mode, phrase, expected intent). Phrases use REAL library values where a slot
# needs one (rapsodia is in the catalog; prova echo is the test playlist).
BATTERY: dict[str, list[tuple[str, str, str]]] = {
    "it-IT": [
        # bare infinitives (the trainer-generalization class: metti->mettere
        # works, aggiungi->aggiungere did NOT - live FallbackIntent 2026-09-22)
        ("profile", "mettere rapsodia in coda", "AddToQueueIntent"),
        ("profile", "aggiungere alla playlist prova echo", "AddSongToPlaylistIntent"),
        ("profile", "riprodurre musica a caso", "PlayRandomIntent"),
        ("profile", "riprendi", "AMAZON.ResumeIntent"),
        # full one-shot wrappers (invocation layer + infinitive)
        ("simulate", "chiedi a mia collezione di riprodurre musica a caso", "PlayRandomIntent"),
        ("simulate", "chiedi a mia collezione di mettere rapsodia in coda", "AddToQueueIntent"),
        ("simulate", "chiedi a mia collezione di riprodurre rapsodia dopo", "PlayNextIntent"),
        # JF-887/JF-902: PRE-DEPLOY WINDOW for the ON rows. The article-less
        # 'attivare loop' tail had no loop-on row, so the wrapper fell to
        # LoopAllOffIntent (live 2026-10-14) and profile-nlu read the same
        # steal on the bare tail; it stays red until the orchestrator deploys
        # the rebuilt it-IT model (DLL embedded template + rebuild endpoint).
        # JF-902 flipped the expectation: the bare loop-activation rows moved
        # to LoopSongOnIntent (JF-901 made bare 'loop' mean repeat-ONE), so
        # the wrapper now expects LoopSongOnIntent; on the saved model it
        # still reads LoopAllOnIntent until that deploy. The OFF row is the
        # steal control: green pre-deploy (live probe 2026-10-14,
        # LoopAllOffIntent with LoopAllOn considered second) and it must STAY
        # green once the loop-ON side gains its near-identical rows. The
        # neighboring mescolamento row staying green is the marker that only
        # the pending flip is red.
        ("simulate", "chiedi a mia collezione di attivare loop", "LoopSongOnIntent|AMAZON.LoopOnIntent"),
        ("simulate", "chiedi a mia collezione di disattivare loop", "LoopAllOffIntent"),
        ("simulate", "chiedi a mia collezione di attivare il mescolamento", "ShuffleAllOnIntent"),
        # JF-889: the article-tail twins, the wrapper shape of the live-failing
        # article forms ('attiva il loop' shuffle-stole live). RED-UNTIL-DEPLOY
        # since the JF-902 rebuild (the suffix rows moved to LoopSongOnIntent):
        # green again only once the JF-902 model is deployed; the article-less
        # rows above are the evergreen controls.
        ("simulate", "chiedi a mia collezione di attivare il loop", "LoopSongOnIntent|AMAZON.LoopOnIntent"),
        ("simulate", "chiedi a mia collezione di disattivare il loop", "LoopAllOffIntent"),
        # JF-874: the film wrapper routes the catalog-backed MovieTitle twin
        # (PlayMovieIntent, served by the PlayVideo handler); the expectation
        # flips from PlayVideoIntent with the deployed model + movie catalog
        # sync. 'ada' is a real library movie (the battery's slot convention).
        # PRE-DEPLOY WINDOW: both rows read red until the orchestrator deploys
        # the rebuilt it-IT model AND the next catalog sync pins the MovieTitle
        # version (the JF-684 live-effect path); the neighboring wrapper rows
        # staying green is the marker that only the pending flip is red.
        # 2026-10-14: 'ada' is NOT in the MovieTitle catalog (a non-catalog
        # title), so the wrapper may raw-fill through EITHER arm of the split:
        # PlayMovieIntent (the catalog-carrier arm, what profile-nlu resolves
        # 3/3) or PlayVideoIntent (the SearchQuery arm, what simulate's ASR
        # picks). Both PLAY the film (PlayVideoIntentHandler serves both since
        # JF-874); the row asserts the wrapper reaches the skill and plays a
        # video family intent, so both are accepted (the JF-298 divergence).
        ("simulate", "chiedi a mia collezione di riprodurre il film ada", "PlayMovieIntent|PlayVideoIntent"),
        ("simulate", "chiedi a mia collezione di mettere il film ada", "PlayMovieIntent|PlayVideoIntent"),
        ("simulate", "chiedi a mia collezione di riprodurre brani simili", "PlayRadioIntent"),
        ("simulate", "chiedi a mia collezione di fermare dopo un minuto", "SleepTimerIntent"),
        ("simulate", "chiedi a mia collezione di aggiungere alla playlist prova echo", "AddSongToPlaylistIntent"),
        ("simulate", "chiedi a mia collezione di riprodurre il podcast generazione", "PlayPodcastIntent"),
        ("simulate", "chiedi a mia collezione di leggere il libro il gattopardo", "PlayBookIntent"),
    ],
    # JF-551 (2026-10-04): the es SUBJUNCTIVE episode wrapper payload ("pide a
    # {inv} que + subjunctive"; the 2026-09-13 infinitive twin was the wrong
    # morphology and was reverted). profile rows only: the bare payload needs
    # no invocation layer, so the evergreen control does not apply. The battery
    # asserts INTENT selection only; the verb-glue pollution (series_name
    # "reproduzca {series}") is pinned by the exact-value fixture rows in
    # tests/integration/fixtures/es-*.yaml, which stay red until the rebuilt
    # models with the "reproduzca" family are deployed. "the bear" is a real
    # library series (the battery convention for slot values).
    "es-ES": [
        ("profile", "reproduzca the bear temporada uno episodio tres", "PlayEpisodeIntent"),
        # the connector-residue twin (the wrapper's "que" may ride into the
        # payload; see the es-ES fixture comment for the open verification)
        ("profile", "que reproduzca the bear temporada uno episodio tres", "PlayEpisodeIntent"),
        # JF-877 (2026-10-20): the es skip wrapper-tail rows, PROFILE payloads
        # (the connector-residue "que salte" shape, this section's own
        # convention: the es one-shot invocation layer is unreliable, so no
        # simulate rows here - code-review round 3 F1). Pre-deploy these read
        # no-selection against the JF-875-only model; the fixtures pin the
        # COMPOSED "pide a mi colección ..." forms. The amount row uses the
        # probe's own amount-first phrasing.
        ("profile", "que salte hacia atrás", "SkipForwardBackIntent"),
        ("profile", "que salte treinta segundos hacia atrás", "SkipForwardBackIntent"),
    ],
    "es-MX": [
        ("profile", "reproduzca the bear temporada uno episodio tres", "PlayEpisodeIntent"),
    ],
    "es-US": [
        ("profile", "reproduzca the bear temporada uno episodio tres", "PlayEpisodeIntent"),
    ],
    # JF-877 (2026-10-20): the fr composed skip wrappers, SIMULATE rows (the
    # gap lives at the invocation layer: the bare payloads "sauter en arriere"
    # and "sauter en avant de trente secondes" already route on profile-nlu,
    # but the composed one-shots read FallbackIntent pre-deploy). Red until
    # the DLL embeds the "de sauter {seek_direction}" row and the fr models
    # are rebuilt; simulate rows exercise the invocation consumption that
    # profile-nlu cannot.
    "fr-FR": [
        ("simulate", "demande à mon serveur de sauter en arrière", "SkipForwardBackIntent"),
        ("simulate", "demande à mon serveur de sauter en avant de trente secondes", "SkipForwardBackIntent"),
    ],
    # JF-877 (2026-10-20): the nl skip wrapper-tail rows, PROFILE payloads (the
    # om-less te-springen twins; profile-nlu 2026-10-20 00:25, skill 33dfacd5,
    # read BOTH composed one-shots FallbackIntent with the CORRECT invocation
    # name, while the bare "spring 30 seconden vooruit" routes). PROFILE not
    # simulate: the nl one-shot invocation layer is documented-unreliable
    # (SmapiClient.INVOCATION_PREFIX notes), so no evergreen one-shot control
    # exists for this locale (code-review round 3 F1); the fixtures pin the
    # composed "vraag mijn collectie ..." forms. Word-form amount only: this
    # locale's profile-nlu fails DIGIT delivery into AMAZON.NUMBER slots (the
    # fixture-documented quirk).
    "nl-NL": [
        ("profile", "vooruit te springen", "SkipForwardBackIntent"),
        ("profile", "dertig seconden vooruit te springen", "SkipForwardBackIntent"),
    ],
}

# The simulate-outage controls: per-locale one-shots that have routed
# on-device/live every time they were tried (the it-IT phrase is the original
# device-proven control; fr carries the JF-900 live-verified composition).
# When a locale's control fails to invoke, simulate is down for THAT locale and
# its simulate rows skip. A locale whose section carries simulate rows but has
# no control here is a configuration error (exit 2), never a silent skip.
EVERGREEN_CONTROLS = {
    "it-IT": "chiedi a mia collezione di suonare i radiohead",
    "fr-FR": "demande à mon serveur de lire mes favoris",
}


def _run_ask(args: list[str]) -> subprocess.CompletedProcess:
    return subprocess.run(args, capture_output=True, text=True, timeout=120)


def default_skill_id() -> str | None:
    """Discover the current skill id via the ask CLI (NEVER cache it).

    The vendor skill LIST carries an empty skillManifest and this ask CLI has no
    get-skill, so the discriminator is the it-IT interaction model: only this
    plugin's skill declares the JellyfinArtist custom slot type.
    """
    try:
        out = _run_ask(["ask", "smapi", "list-skills-for-vendor"]).stdout
        ids = [s.get("skillId") for s in json.loads(out).get("skills", []) if s.get("skillId")]
    except Exception as exc:  # noqa: BLE001 - report and bail, no skill guesswork
        print(f"ERROR: could not list skills ({exc})")
        return None

    for skill_id in ids:
        result = _run_ask(["ask", "smapi", "get-interaction-model", "--skill-id", skill_id,
                           "--stage", "development", "--locale", "it-IT"])
        if "jellyfinartist" in result.stdout.lower():
            return skill_id
    return None


def profile_nlu(skill_id: str, locale: str, phrase: str) -> str | None:
    result = _run_ask(["ask", "smapi", "profile-nlu", "--skill-id", skill_id,
                       "--stage", "development", "--locale", locale, "--utterance", phrase])
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError:
        return None
    # selectedIntent lives at the TOP level (memory: result.intent yields false
    # all-NO_SELECTION readings).
    return (data.get("selectedIntent") or {}).get("name")


def simulate_intent(skill_id: str, locale: str, phrase: str) -> tuple[bool, str | None]:
    """Full-pipeline simulate. Returns (invoked, intent name or None).

    2026-10-14: the ask CLI REMOVED the --locale option from simulate-skill
    (passing it errors with "unknown option '--locale'", which this script
    misread as the simulate outage for ~30h: the init call failed before any
    simulation started). --device-locale is the surviving option.
    """
    init = _run_ask(["ask", "smapi", "simulate-skill", "--skill-id", skill_id,
                     "--stage", "development", "--device-locale", locale,
                     "--input-content", phrase])
    try:
        sim = json.loads(init.stdout)
    except json.JSONDecodeError:
        return (False, None)
    sim_id = sim.get("id")
    if not sim_id:
        return (False, None)
    for _ in range(20):
        time.sleep(2)
        poll = _run_ask(["ask", "smapi", "get-skill-simulation", "--skill-id", skill_id,
                         "--simulation-id", sim_id, "--stage", "development"])
        try:
            data = json.loads(poll.stdout)
        except json.JSONDecodeError:
            continue
        body = data.get("body", data)
        result = body.get("result", body)
        status = body.get("status") or data.get("status")
        if status in ("SUCCESSFUL", "FAILED"):
            invocations = []
            if isinstance(result, dict):
                # 2026-10-14: the simulation result moved to the invocations
                # ARRAY under skillExecutionInfo (verified live; the old flat
                # skillInvocationInfo key disappeared, which the 2026-10-13/14
                # "outage" actually was - a parser drift, not Amazon downtime).
                invocations = (result.get("skillExecutionInfo") or {}).get("invocations") or []
                # Backward compatibility with the pre-drift flat shape.
                if not invocations and result.get("skillInvocationInfo"):
                    invocations = [result["skillInvocationInfo"]]
            intent = None
            if invocations:
                # The intent name sits inside the invocation payload's request.
                payload = (invocations[0].get("invocationRequest") or {}).get("body", {}).get("request", {})
                intent = (payload.get("intent") or {}).get("name")
            return (bool(invocations), intent)
    return (False, None)


def main() -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("-l", "--locale", default="it-IT", choices=sorted(BATTERY))
    parser.add_argument("--skill-id", default=None)
    args = parser.parse_args()

    skill_id = args.skill_id or default_skill_id()
    if not skill_id:
        print("ERROR: no Jellyfin skill found for this vendor; pass --skill-id")
        return 2

    phrases = BATTERY.get(args.locale)
    if not phrases:
        print(f"ERROR: no battery defined for {args.locale} yet")
        return 2

    delay = float(os.environ.get("SMAPI_DELAY", "1.5"))

    simulate_rows = [row for row in phrases if row[0] == "simulate"]
    outage = False
    if simulate_rows:
        control_phrase = EVERGREEN_CONTROLS.get(args.locale)
        if not control_phrase:
            print(f"ERROR: {args.locale} carries simulate rows but no evergreen "
                  "control is known for it (add one to EVERGREEN_CONTROLS or "
                  "convert the rows to profile payloads); refusing to run them "
                  "as silent skips")
            return 2
        control_invoked, _ = simulate_intent(skill_id, args.locale, control_phrase)
        outage = not control_invoked
        if outage:
            print(f"# OUTAGE: evergreen control failed to invoke; {len(simulate_rows)} "
                  "wrapper rows SKIP (memory: simulate_outage_per_locale)")

    failures = 0
    skipped = 0
    print(f"# Wrapper battery {args.locale} (skill {skill_id})")
    for mode, phrase, expected in phrases:
        if mode == "simulate":
            if outage:
                skipped += 1
                print(f"OUTAGE-SKIP  {phrase!r}")
                continue
            invoked, intent = simulate_intent(skill_id, args.locale, phrase)
            got = intent if invoked else "NOT-INVOKED"
            time.sleep(delay)
        else:
            got = profile_nlu(skill_id, args.locale, phrase)
            time.sleep(delay)

        # Alternation support ("A|B"): the row passes when the routing lands on
        # any listed intent (used for shapes whose engine legitimately differs,
        # e.g. the catalog-carrier vs SearchQuery arms of the JF-874 split).
        status = "PASS" if got in expected.split("|") else "FAIL"
        if status == "FAIL":
            failures += 1
        print(f"{status}  {phrase!r} -> {got} (expected {expected})")

    print(f"\n{len(phrases) - failures - skipped}/{len(phrases)} passed"
          + (f", {skipped} skipped (simulate outage)" if skipped else ""))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

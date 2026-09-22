#!/usr/bin/env python3
"""Wrapper-phrase NLU battery (2026-09-22, Paolo's directive after the fourth
wrapper-form gap in two days).

Two verification modes, because profile-nlu and simulate-skill see different
layers (verified live 2026-09-22):

- "profile" rows carry NO invocation prefix. profile-nlu matches the raw text
  against the saved model, which is exactly right for these (bare infinitives,
  imperative forms). The prefix words of a wrapper phrase are NOT model samples,
  so feeding a prefixed phrase to profile-nlu fails BY DESIGN - never do that.
- "simulate" rows carry the full one-shot wrapper ("chiedi a mia collezione
  di ..."). Only the simulate pipeline models the invocation-matching layer
  that strips the prefix. These rows run an evergreen control first; when the
  control itself fails to invoke, the per-locale simulate outage (memory:
  simulate_outage_per_locale; recurring 09-13/14/21-22) is active and the
  wrapper rows SKIP with an OUTAGE marker (not FAIL).

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
        ("simulate", "chiedi a mia collezione di attivare loop", "LoopAllOnIntent"),
        ("simulate", "chiedi a mia collezione di riprodurre brani simili", "PlayRadioIntent"),
        ("simulate", "chiedi a mia collezione di fermare dopo un minuto", "SleepTimerIntent"),
        ("simulate", "chiedi a mia collezione di aggiungere alla playlist prova echo", "AddSongToPlaylistIntent"),
        ("simulate", "chiedi a mia collezione di riprodurre il podcast generazione", "PlayPodcastIntent"),
        ("simulate", "chiedi a mia collezione di leggere il libro il gattopardo", "PlayBookIntent"),
    ],
}

# The simulate-outage control: a one-shot that has routed on-device every time
# it was tried. When IT fails to invoke, simulate is down for the locale.
EVERGREEN_CONTROL = "chiedi a mia collezione di suonare i radiohead"


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
    """Full-pipeline simulate. Returns (invoked, intent name or None)."""
    init = _run_ask(["ask", "smapi", "simulate-skill", "--skill-id", skill_id,
                     "--stage", "development", "--device-locale", locale,
                     "--locale", locale, "--input-content", phrase])
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
            inv = result.get("skillInvocationInfo") if isinstance(result, dict) else None
            intent = None
            if inv:
                # The intent name sits inside the invocation payload's request.
                payload = inv.get("invocationRequest", {}).get("body", {}).get("request", {})
                intent = (payload.get("intent") or {}).get("name")
            return (bool(inv), intent)
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
        control_invoked, _ = simulate_intent(skill_id, args.locale, EVERGREEN_CONTROL)
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

        status = "PASS" if got == expected else "FAIL"
        if got != expected:
            failures += 1
        print(f"{status}  {phrase!r} -> {got} (expected {expected})")

    print(f"\n{len(phrases) - failures - skipped}/{len(phrases)} passed"
          + (f", {skipped} skipped (simulate outage)" if skipped else ""))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

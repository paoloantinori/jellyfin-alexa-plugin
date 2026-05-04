"""SMAPI client wrapping ASK CLI simulate-skill for NLU testing."""

from __future__ import annotations

import json
import subprocess
import time
from typing import Any


class SmapiError(Exception):
    """Raised when an ASK CLI invocation fails."""


# Module-level rate-limit state shared across all SmapiClient instances
# within the same process.  This ensures rate limiting works correctly
# even though each test creates a fresh SmapiClient.
_last_smapi_call: float = 0.0


class SmapiClient:
    """Wrapper for ASK CLI simulate-skill SMAPI endpoint.

    Provides rate-limited access to Alexa NLU simulation, parsing
    the response into structured intent and slot data for test assertions.
    """

    def __init__(self, skill_id: str, locale: str, delay: float = 1.5):
        self.skill_id = skill_id
        self.locale = locale
        self.delay = delay

    def simulate(self, utterance: str, stage: str = "development") -> dict[str, Any]:
        """Run simulate-skill and return parsed JSON response.

        Respects rate limiting by enforcing minimum delay between calls.
        """
        global _last_smapi_call
        elapsed = time.time() - _last_smapi_call
        if elapsed < self.delay:
            time.sleep(self.delay - elapsed)

        cmd = [
            "ask",
            "smapi",
            "simulate-skill",
            "--skill-id",
            self.skill_id,
            "--locale",
            self.locale,
            "--stage",
            stage,
            "--input",
            utterance,
            "--simulation-type",
            "DEFAULT",
        ]

        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=60,
        )

        _last_smapi_call = time.time()

        if result.returncode != 0:
            raise SmapiError(
                f"ASK CLI failed (exit {result.returncode}): {result.stderr}"
            )

        try:
            return json.loads(result.stdout)
        except json.JSONDecodeError:
            # ask smapi may output status messages before JSON;
            # scan lines for the first JSON object.
            for line in result.stdout.splitlines():
                line = line.strip()
                if line.startswith("{"):
                    return json.loads(line)
            raise SmapiError(
                f"Could not parse JSON from output: {result.stdout[:500]}"
            )

    @staticmethod
    def parse_nlu_result(response: dict[str, Any]) -> dict[str, Any]:
        """Extract intent and slots from simulate-skill response.

        Returns:
            dict with keys:
                intent: resolved intent name (str)
                slots:  dict mapping slot_name to
                        {"name": str, "value": str, "resolutions": list}
                confidence: NLU confidence score (float)
                raw:   full response payload for debugging
                error: present only when parsing failed (str)
        """
        try:
            result = response.get("result", response)
            skill_simulation = result.get("skillSimulation", result)
            simulations = skill_simulation.get("simulations", [skill_simulation])
            sim = simulations[0] if simulations else skill_simulation

            nlu = sim.get("alexaExecutionInfo", {}).get("nlu", {})
            intent_info = nlu.get("intent", {})
            intent = intent_info.get("name", "")
            confidence = intent_info.get("confidence", {}).get("score", 0.0)

            slots: dict[str, dict[str, Any]] = {}
            for slot in nlu.get("slots", []):
                slots[slot["name"]] = {
                    "name": slot["name"],
                    "value": slot.get("value", ""),
                    "resolutions": slot.get("resolutions", []),
                }

            return {
                "intent": intent,
                "slots": slots,
                "confidence": confidence,
                "raw": response,
            }
        except (KeyError, IndexError, TypeError) as exc:
            raise SmapiError(
                f"Unexpected SMAPI response structure: {exc}"
            ) from exc

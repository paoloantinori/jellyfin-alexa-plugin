"""SMAPI client wrapping ASK CLI simulate-skill for NLU testing."""

from __future__ import annotations

import json
import logging
import subprocess
import time
from typing import Any

logger = logging.getLogger("nlu.smapi")


class SmapiError(Exception):
    """Raised when an ASK CLI invocation fails."""


class SmapiRateLimitError(SmapiError):
    """Raised when SMAPI returns a rate-limit or throttling error."""


# Module-level rate-limit state shared across all SmapiClient instances
# within the same process.  This ensures rate limiting works correctly
# even though each test creates a fresh SmapiClient.
_last_smapi_call: float = 0.0

POLL_INTERVAL = 3.0
POLL_TIMEOUT = 60.0
MAX_POLL_ATTEMPTS = 15

# Rate-limit backoff settings
_RATE_LIMIT_BACKOFF_BASE = 5.0
_RATE_LIMIT_MAX_RETRIES = 3


def _run_ask(args: list[str]) -> str:
    """Run an ASK CLI command and return stdout. Raises SmapiError on failure."""
    logger.debug("ask smapi %s", " ".join(args))
    result = subprocess.run(
        ["ask", "smapi"] + args,
        capture_output=True,
        text=True,
        timeout=60,
    )
    if result.returncode != 0:
        stderr = result.stderr
        if "429" in stderr or "throttl" in stderr.lower() or "rate" in stderr.lower():
            raise SmapiRateLimitError(
                f"SMAPI rate limited (exit {result.returncode}): {stderr[:300]}"
            )
        raise SmapiError(
            f"ASK CLI failed (exit {result.returncode}): {stderr[:500]}"
        )
    return result.stdout


def _parse_json_output(raw: str) -> dict[str, Any]:
    """Parse JSON from ASK CLI output, scanning past any non-JSON lines."""
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        for line in raw.splitlines():
            line = line.strip()
            if line.startswith("{"):
                return json.loads(line)
    raise SmapiError(f"Could not parse JSON from output: {raw[:500]}")


class SmapiClient:
    """Wrapper for ASK CLI simulate-skill SMAPI endpoint.

    Provides rate-limited access to Alexa NLU simulation, parsing
    the response into structured intent and slot data for test assertions.
    """

    def __init__(self, skill_id: str, locale: str, delay: float = 1.5,
                 invocation_name: str = ""):
        self.skill_id = skill_id
        self.locale = locale
        self.delay = delay
        self.invocation_name = invocation_name

    def simulate(self, utterance: str, stage: str = "development") -> dict[str, Any]:
        """Run simulate-skill and return the completed simulation result.

        If invocation_name is set, the utterance is prefixed with
        "ask <invocation_name> to " so the simulation resolves to this skill.
        """
        global _last_smapi_call
        elapsed = time.time() - _last_smapi_call
        if elapsed < self.delay:
            wait = self.delay - elapsed
            logger.debug("Rate-limit delay: %.1fs", wait)
            time.sleep(wait)

        full_utterance = utterance
        if self.invocation_name:
            full_utterance = f"ask {self.invocation_name} to {utterance}"

        # Initiate simulation with rate-limit retry
        init_response = self._initiate_simulation(full_utterance, stage)
        simulation_id = init_response.get("id", "")
        status = init_response.get("status", "")

        if status in ("SUCCESSFUL", "FAILED"):
            _last_smapi_call = time.time()
            return init_response

        if not simulation_id:
            raise SmapiError(
                f"No simulation ID returned: {json.dumps(init_response)[:500]}"
            )

        logger.info(
            "Simulation %s started for [%s] (%s), polling...",
            simulation_id[:12], utterance[:40], self.locale,
        )

        # Poll until completion with progress logging
        return self._poll_simulation(simulation_id, utterance)

    def _initiate_simulation(
        self, full_utterance: str, stage: str
    ) -> dict[str, Any]:
        """Initiate a simulation with automatic rate-limit retry."""
        for attempt in range(_RATE_LIMIT_MAX_RETRIES):
            try:
                init_output = _run_ask([
                    "simulate-skill",
                    "--skill-id", self.skill_id,
                    "--device-locale", self.locale,
                    "--stage", stage,
                    "--input-content", full_utterance,
                ])
                return _parse_json_output(init_output)
            except SmapiRateLimitError as exc:
                backoff = _RATE_LIMIT_BACKOFF_BASE * (attempt + 1)
                if attempt < _RATE_LIMIT_MAX_RETRIES - 1:
                    logger.warning(
                        "Rate limited on attempt %d/%d, backing off %.0fs: %s",
                        attempt + 1, _RATE_LIMIT_MAX_RETRIES, backoff,
                        str(exc)[:100],
                    )
                    time.sleep(backoff)
                    continue
                raise SmapiError(
                    f"Rate limited after {_RATE_LIMIT_MAX_RETRIES} attempts"
                ) from exc
        raise SmapiError("Should not reach here")

    def _poll_simulation(
        self, simulation_id: str, utterance: str
    ) -> dict[str, Any]:
        """Poll get-skill-simulation until completion with progress output."""
        global _last_smapi_call
        deadline = time.time() + POLL_TIMEOUT
        attempts = 0

        while time.time() < deadline and attempts < MAX_POLL_ATTEMPTS:
            attempts += 1
            time.sleep(POLL_INTERVAL)

            try:
                poll_output = _run_ask([
                    "get-skill-simulation",
                    "--skill-id", self.skill_id,
                    "--simulation-id", simulation_id,
                ])
            except SmapiRateLimitError as exc:
                remaining = max(0, deadline - time.time())
                logger.warning(
                    "Rate limited while polling [%s] (%.0fs remaining): %s",
                    utterance[:30], remaining, str(exc)[:80],
                )
                if remaining < _RATE_LIMIT_BACKOFF_BASE:
                    raise SmapiError(
                        f"Rate limited with only {remaining:.0f}s left for "
                        f"'{utterance}' ({self.locale})"
                    ) from exc
                time.sleep(_RATE_LIMIT_BACKOFF_BASE)
                continue

            poll_response = _parse_json_output(poll_output)
            status = poll_response.get("status", "")
            elapsed = time.time() - (deadline - POLL_TIMEOUT)

            if status == "SUCCESSFUL":
                _last_smapi_call = time.time()
                logger.info(
                    "Simulation resolved after %.1fs: [%s] -> %s",
                    elapsed, utterance[:30],
                    self._intent_summary(poll_response),
                )
                return poll_response

            if status == "FAILED":
                _last_smapi_call = time.time()
                error_msg = (
                    poll_response.get("result", {})
                    .get("error", {})
                    .get("message", "")
                )
                raise SmapiError(
                    f"Simulation failed after {elapsed:.1f}s for '{utterance}' "
                    f"({self.locale}): {error_msg}"
                )

            logger.debug(
                "  poll #%d [%s] status=%s (%.1fs elapsed)",
                attempts, utterance[:30], status, elapsed,
            )

        _last_smapi_call = time.time()
        raise SmapiError(
            f"Simulation timed out after {POLL_TIMEOUT:.0f}s / {attempts} polls "
            f"for '{utterance}' ({self.locale})"
        )

    @staticmethod
    def _intent_summary(response: dict[str, Any]) -> str:
        """Extract a short intent name for log messages."""
        try:
            considered = (
                response.get("result", response)
                .get("alexaExecutionInfo", {})
                .get("consideredIntents", [])
            )
            return considered[0].get("name", "?") if considered else "?"
        except (KeyError, IndexError):
            return "?"

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
        """
        try:
            result = response.get("result", response)
            alexa_info = result.get("alexaExecutionInfo", {})
            considered = alexa_info.get("consideredIntents", [])

            if not considered:
                raise SmapiError("No consideredIntents in response")

            # Use the first (highest-confidence) intent
            primary = considered[0]
            intent = primary.get("name", "")
            confidence = 0.0

            # Try to get confidence from nlu section if present
            nlu = alexa_info.get("nlu", {})
            if nlu:
                intent_info = nlu.get("intent", {})
                if intent_info:
                    intent = intent_info.get("name", intent)
                    confidence = intent_info.get("confidence", {}).get("score", 0.0)

            slots: dict[str, dict[str, Any]] = {}
            for slot_name, slot_data in primary.get("slots", {}).items():
                slots[slot_name] = {
                    "name": slot_name,
                    "value": slot_data.get("value", ""),
                    "resolutions": slot_data.get("resolutions", []),
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

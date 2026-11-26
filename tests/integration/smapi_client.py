"""SMAPI client wrapping ASK CLI profile-nlu and simulate-skill for NLU testing."""

from __future__ import annotations

import json
import logging
import subprocess
import time
from typing import Any

logger = logging.getLogger("nlu.smapi")


class SmapiError(Exception):
    """Raised when an ASK CLI invocation fails."""


class MalformedResponseError(Exception):
    """A response whose SHAPE is unusable (a truthy non-dict selectedIntent
    and similar). Deliberately NOT a SmapiError subclass: the battery runner
    overloads SmapiError from parse_profile_nlu_result as NO_SELECTION
    (judged against expectations), so a malformed shape must stay
    distinguishable and map to an ERROR row instead (JF-936 code review:
    an unmarked degrade reads as an NLU-selection regression in triage)."""


class SmapiRateLimitError(SmapiError):
    """Raised when SMAPI returns a rate-limit or throttling error."""


class SmapiServerError(SmapiError):
    """Raised when SMAPI returns a transient 5xx server error.

    Observed on profile-nlu (JF-406): single 500s fail cases that pass in
    isolation, so these are retried like rate limits.
    """


class SmapiUnexpectedError(SmapiError):
    """A simulation resolved FAILED with the platform's generic cause.

    "An unexpected error occurred": the chronic open-simulation outage
    class (JF-511's ~21s 'unexpected error'; the recurring per-locale
    simulate outage whose signature is opens dead while one-shot
    simulations work). Plain SmapiError to every other handler; the e2e
    smoke harness catches the type to fall back to a one-shot instead of
    failing the test (JF-656).
    """


class SmapiSelectionLostError(SmapiError):
    """A simulation resolved FAILED with the skill-selection loss class.

    "This utterance did not resolve to any intent in your skill" (with
    consideredIntents [<IntentForDifferentSkill>...]): the simulated
    device's skill router considered the utterance and picked ANOTHER
    skill. Unlike the chronic outage class this one is intermittent PER
    CALL: the same (locale, utterance) went FAILED then SUCCESSFUL within
    15 minutes on the same token and pipeline (research 2026-10-17,
    claudedocs/research_simulate-routing_2026-10-17.md). Plain SmapiError
    to every other handler, so in-session command steps keep hard-failing
    on it; the e2e smoke harness catches the type on the OPEN step only,
    to fall back to a one-shot instead of failing the row (JF-899).
    """


# Status phrases and codes SMAPI/ASK CLI surfaces for transient 5xx
# failures.  Matched against stderr; phrases over bare codes because a
# bare "500" can appear in unrelated error bodies.
_SERVER_ERROR_MARKERS = (
    "internal server error",
    "service unavailable",
    "bad gateway",
    "gateway timeout",
    "internalservererror",
    "serviceunavailable",
    " 500",
    " 502",
    " 503",
    " 504",
)

# The platform's generic cause text for a FAILED simulation (JF-656).
_OUTAGE_ERROR_MARKER = "an unexpected error occurred"

# The platform's selection-loss cause text for a FAILED simulation
# (JF-899).  Kept to the stable shared prefix: the 2026-09-22 probe
# recorded the message truncated one word earlier.
_SELECTION_LOST_ERROR_MARKER = "did not resolve to any intent"


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


def _run_ask(args: list[str], timeout: float = 60) -> str:
    """Run an ASK CLI command and return stdout. Raises SmapiError on failure."""
    logger.debug("ask smapi %s", " ".join(args))
    result = subprocess.run(
        ["ask", "smapi"] + args,
        capture_output=True,
        text=True,
        timeout=timeout,
    )
    if result.returncode != 0:
        stderr = result.stderr
        if "429" in stderr or "throttl" in stderr.lower() or "rate" in stderr.lower():
            raise SmapiRateLimitError(
                f"SMAPI rate limited (exit {result.returncode}): {stderr[:300]}"
            )
        stderr_lower = stderr.lower()
        if any(marker in stderr_lower for marker in _SERVER_ERROR_MARKERS):
            raise SmapiServerError(
                f"SMAPI server error (exit {result.returncode}): {stderr[:300]}"
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


def _slot_resolutions(slot: Any) -> list[dict]:
    """The per-authority ER records of one considered-slot object.

    A list of {authority, code, canonical?, matches?} dicts: canonical is the
    first matched value (the JF-659 contract), matches the FULL list when
    an authority matched more than one (the JF-690 multi-value class).
    Anything unexpected
    (missing resolutions, non-dict authority, empty values) degrades to
    what is verifiably there; this helper never raises.
    """
    records: list[dict] = []
    resolutions = slot.get("resolutions") if isinstance(slot, dict) else None
    if not isinstance(resolutions, dict):
        return records
    authorities = resolutions.get("resolutionsPerAuthority") or []
    if not isinstance(authorities, list):
        return records
    for auth in authorities:
        if not isinstance(auth, dict):
            continue
        status = auth.get("status")
        code = status.get("code") if isinstance(status, dict) else None
        rec: dict = {"authority": auth.get("authority"), "code": code}
        if code == "ER_SUCCESS_MATCH":
            names = []
            for v in (auth.get("values") or []):
                if isinstance(v, dict) and isinstance(v.get("value"), dict):
                    name = v["value"].get("name")
                    if name is not None:
                        names.append(name)
            if names:
                # canonical mirrors the JF-659 C# contract (Values[0]);
                # matches carries EVERY matched value so the multi-value
                # ER class (JF-690/JF-885: 'pink' -> P!nk + Pink Floyd)
                # stays visible in the capture, not just rank #1.
                rec["canonical"] = names[0]
                if len(names) > 1:
                    rec["matches"] = names
        records.append(rec)
    return records


def considered_hypotheses(response: dict) -> list[dict]:
    """The FULL consideredIntents hypotheses (JF-968): the ONE extractor.

    Home is this module (the layer every consumer already imports; the lab
    re-exports it from smapi_ops so both spellings stay reachable). One
    dict per entry, order preserved (the SAME intent can appear multiple
    times with different slot segmentations; entry rank is as received,
    unverified). Live shape (probed 2026-11-25): each entry is a full
    Intent object {name, slots: {slot_name: {value, resolutions?}}};
    there is NO score field. Per entry: {"intent": name, "slots":
    {slot: value}, "er": {slot: [{authority, code, canonical?}]}}.
    Guarded, never raises: a malformed entry or slot degrades to empty
    fields instead of aborting the battery (a non-dict slot reads as
    value None, never the raw object). The names-only projections (the
    battery row column, the no-selection error message) derive from this
    one list.
    """
    out: list[dict] = []
    for entry in response.get("consideredIntents") or []:
        if not isinstance(entry, dict):
            continue
        slots: dict = {}
        er: dict = {}
        raw_slots = entry.get("slots")
        if isinstance(raw_slots, dict):
            for slot_name, slot in raw_slots.items():
                slots[str(slot_name)] = (
                    slot.get("value") if isinstance(slot, dict) else None
                )
                per_authority = _slot_resolutions(slot)
                if per_authority:
                    er[str(slot_name)] = per_authority
        out.append({"intent": entry.get("name"), "slots": slots, "er": er})
    return out


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

    # One-shot wrapper status (live probes 2026-09-13, JF-551; see the
    # locale-routing-probe skill): it-IT and en-* wrappers function. de-DE "sag
    # {inv} {payload}" functioned once of four (English invocation name is
    # marginally recognized under non-English ASR). nl-NL and ja-JP invocations
    # reach the skill but payload routing under the wrapper was not stable.
    # NO working one-shot composition found for es-ES/es-MX/es-US, pt-BR,
    # hi-IN, ar-SA - even favorites-payload controls failed (invocation layer,
    # not the payload). For in-session testing in those locales use the e2e
    # smoke two-step convention (locale open verb, then the bare command).
    INVOCATION_PREFIX: dict[str, str] = {
        "en-US": "ask {inv} to ",
        "en-GB": "ask {inv} to ",
        "en-IN": "ask {inv} to ",
        "en-AU": "ask {inv} to ",
        "en-CA": "ask {inv} to ",
        "it-IT": "chiedi a {inv} di ",
        "fr-FR": "demande à {inv} de ",
        "fr-CA": "demande à {inv} de ",
        "de-DE": "frage {inv} nach ",
        "es-ES": "pide a {inv} que ",
        "es-MX": "pide a {inv} que ",
        "es-US": "pide a {inv} que ",
        "ja-JP": "{inv} を開いて ",
        "pt-BR": "peça para {inv} ",
        # JF-400 2026-08-29: these three were missing and fell back to the English
        # "ask {inv} to ", which misroutes Arabic utterances (live: 'shughl album queen'
        # went to PlayVideoIntent under the English prefix, PlayAlbumIntent bare).
        "ar-SA": "اطلب من {inv} ",
        "nl-NL": "vraag {inv} om ",
        "hi-IN": "{inv} से कहो ",
    }

    # JF-900: fr one-shot FALLBACK composition grammar.  The fr prefix ends
    # in "de ", which wants the INFINITIVE, but the smoke fixtures' commands
    # are bare imperatives; the flat composition ("demande à mon serveur de
    # lis ...") is ungrammatical and lost skill selection live (2026-10-17)
    # while the manual infinitive form ("... de lire ...") routed.
    FR_FALLBACK_INFINITIVE_LOCALES = frozenset({"fr-FR", "fr-CA"})

    FR_FALLBACK_INFINITIVE: dict[str, str] = {
        "lis": "lire",
        "joue": "jouer",
        "mets": "mettre",
        "met": "mettre",
        "ferme": "fermer",
        "arrete": "arrêter",
        "ouvre": "ouvrir",
    }

    @staticmethod
    def compose_fallback_one_shot(utterance: str, locale: str) -> str:
        """Rewrite a leading imperative to its infinitive for a fr one-shot.

        "demande à {inv} de " + <infinitive> is the grammatical composition
        the live evidence routed on.  Applied ONLY at the smoke
        open-fallback construction site (test_e2e.py), never inside
        simulate(): a full-chain fixture row's own utterance is authored
        and must pass through byte-identical.  Unknown leading verbs and
        non-fr locales pass through unchanged (safe default).
        """
        if locale not in SmapiClient.FR_FALLBACK_INFINITIVE_LOCALES:
            return utterance
        head, sep, tail = utterance.partition(" ")
        infinitive = SmapiClient.FR_FALLBACK_INFINITIVE.get(head.lower())
        if infinitive is None:
            return utterance
        return infinitive + sep + tail

    def simulate(self, utterance: str, stage: str = "development") -> dict[str, Any]:
        """Run simulate-skill and return the completed simulation result.

        If invocation_name is set, the utterance is prefixed with a
        locale-aware invocation pattern so the simulation resolves to this skill.
        """
        global _last_smapi_call
        elapsed = time.time() - _last_smapi_call
        if elapsed < self.delay:
            wait = self.delay - elapsed
            logger.debug("Rate-limit delay: %.1fs", wait)
            time.sleep(wait)

        full_utterance = utterance
        if self.invocation_name:
            template = self.INVOCATION_PREFIX.get(
                self.locale, "ask {inv} to "
            )
            full_utterance = f"{template.format(inv=self.invocation_name)}{utterance}"

        # Initiate simulation with rate-limit retry
        init_response = self._initiate_simulation(full_utterance, stage)
        simulation_id = init_response.get("id", "")
        status = init_response.get("status", "")

        if status == "SUCCESSFUL":
            _last_smapi_call = time.time()
            return init_response

        # A FAILED that arrives in the initiation response skips the poll
        # loop entirely: classify it here too, or the outage type would
        # never fire on the fast-fail path.
        if status == "FAILED":
            _last_smapi_call = time.time()
            raise self._failed_simulation_error(
                init_response.get("result", {}).get("error", {}).get("message", ""),
                utterance, self.locale, detail="",
            )

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

    @staticmethod
    def _failed_simulation_error(
        error_msg: str, utterance: str, locale: str, detail: str
    ) -> SmapiError:
        """The error for a FAILED simulation, typed for the failure classes.

        The markers are checked against error_msg alone (the platform's
        cause field): the composed message embeds the utterance, whose
        text must never trigger a classification.  The selection-loss
        marker is checked BEFORE the generic outage marker: whenever the
        more specific diagnosis is present it wins (a cause text carrying
        both must not land the locale in the run-long outage flag).
        """
        message = (
            f"Simulation failed {detail}for '{utterance}' ({locale}): {error_msg}"
        )
        error_lower = error_msg.lower()
        if _SELECTION_LOST_ERROR_MARKER in error_lower:
            return SmapiSelectionLostError(message)
        if _OUTAGE_ERROR_MARKER in error_lower:
            return SmapiUnexpectedError(message)
        return SmapiError(message)

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
                raise self._failed_simulation_error(
                    error_msg, utterance, self.locale, f"after {elapsed:.1f}s "
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

    def profile_nlu(self, utterance: str, stage: str = "development") -> dict[str, Any]:
        """Run profile-nlu (synchronous, no invocation prefix needed).

        Preferred over simulate-skill for NLU intent resolution testing
        because it works with bare utterances in all locales without
        requiring a locale-specific invocation prefix.
        """
        global _last_smapi_call
        elapsed = time.time() - _last_smapi_call
        if elapsed < self.delay:
            wait = self.delay - elapsed
            logger.debug("Rate-limit delay: %.1fs", wait)
            time.sleep(wait)

        for attempt in range(_RATE_LIMIT_MAX_RETRIES):
            try:
                output = _run_ask([
                    "profile-nlu",
                    "--skill-id", self.skill_id,
                    "--stage", stage,
                    "--locale", self.locale,
                    "--utterance", utterance,
                ])
                _last_smapi_call = time.time()
                return _parse_json_output(output)
            except (SmapiRateLimitError, SmapiServerError) as exc:
                backoff = _RATE_LIMIT_BACKOFF_BASE * (attempt + 1)
                if attempt < _RATE_LIMIT_MAX_RETRIES - 1:
                    logger.warning(
                        "%s on attempt %d/%d, backing off %.0fs",
                        type(exc).__name__, attempt + 1,
                        _RATE_LIMIT_MAX_RETRIES, backoff,
                    )
                    time.sleep(backoff)
                    continue
                raise SmapiError(
                    f"{type(exc).__name__} after "
                    f"{_RATE_LIMIT_MAX_RETRIES} attempts"
                ) from exc
        raise SmapiError("Should not reach here")

    # Identity reads are a single cheap get-skill-status call that must never
    # stall the suite the way a full simulation poll may: a much shorter
    # subprocess cap than the default 60s bounds the cost of a hung CLI.
    IDENTITY_TIMEOUT_S = 10.0

    def live_model_identity(self) -> str | None:
        """Read-only SMAPI read of this locale's live interaction-model eTag.

        The same get-skill-status read the NLU lab's build wait uses as its
        baseline witness (scripts/nlu-lab/smapi_ops.py): one call, no --stage
        flag, per-locale entry at interactionModel[<locale>] with an optional
        outer body wrapper. The NLU results ledger (JF-954.5) uses it to
        prove whether a rebuild happened during a suite session; it costs at
        most two calls per locale per session under the short
        IDENTITY_TIMEOUT_S cap. Returns None when the read fails, the locale
        entry is absent or malformed, or the eTag is missing: the ledger
        records null and continues.
        """
        try:
            output = _run_ask([
                "get-skill-status",
                "--skill-id", self.skill_id,
            ], timeout=self.IDENTITY_TIMEOUT_S)
            data = _parse_json_output(output)
        except (SmapiError, ValueError, OSError, subprocess.SubprocessError):
            # SubprocessError covers the runner's timeout: a hung ask CLI must
            # read as an unobtainable identity, never propagate into the test
            # session's teardown. The conftest breaker additionally stops
            # calling after repeated nulls, so a throttled CLI cannot cost
            # even the short cap on every locale.
            return None
        if not isinstance(data, dict):
            return None
        body = data.get("body", data)
        models = body.get("interactionModel") if isinstance(body, dict) else None
        if not isinstance(models, dict):
            return None
        entry = models.get(self.locale)
        if not isinstance(entry, dict):
            return None
        etag = entry.get("eTag")
        if etag is None or etag == "":
            return None
        return str(etag)

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
            # JF-936: null and non-dict shapes degrade instead of crashing
            # (a "slots": null intent kept its name but raised
            # None.items(), aborting the caller): an unusable slots value
            # reads as no slots, a non-dict slot entry as an empty value.
            primary_slots = primary.get("slots")
            if isinstance(primary_slots, dict):
                for slot_name, slot_data in primary_slots.items():
                    # One normalization per entry (JF-936 simplify round):
                    # an unusable slot entry reads as empty for BOTH fields.
                    entry = slot_data if isinstance(slot_data, dict) else {}
                    slots[slot_name] = {
                        "name": slot_name,
                        "value": entry.get("value", ""),
                        "resolutions": entry.get("resolutions", []),
                    }

            return {
                "intent": intent,
                "slots": slots,
                "confidence": confidence,
                "raw": response,
            }
        except (KeyError, IndexError, TypeError, AttributeError) as exc:
            raise SmapiError(
                f"Unexpected SMAPI response structure: {exc}"
            ) from exc

    @staticmethod
    def parse_profile_nlu_result(response: dict[str, Any]) -> dict[str, Any]:
        """Extract intent and slots from profile-nlu response.

        Returns:
            dict with keys:
                intent: resolved intent name (str)
                slots:  dict mapping slot_name to
                        {"name": str, "value": str}
                considered: the full consideredIntents hypotheses (list of
                        {intent, slots, er}; [] when none) via the ONE
                        module-level extractor (considered_hypotheses)
                raw:   full response payload for debugging
        """
        selected = response.get("selectedIntent")
        considered = considered_hypotheses(response)
        # JF-936 code review: a truthy NON-dict selectedIntent (malformed
        # response) is neither a crash nor a no-selection verdict: it raises
        # the distinct MalformedResponseError so the battery runner maps it
        # to a MARKED ERROR row instead of an unmarked NO_SELECTION (an
        # unmarked degrade reads as an NLU-selection regression in triage).
        if not isinstance(selected, dict) or not selected:
            if selected:
                raise MalformedResponseError(
                    f"selectedIntent present but not a dict: {type(selected).__name__}")
            # The computed list rides the exception so the caller reads one
            # extraction per row on this branch too (no second walk).
            if considered:
                names = [h.get("intent") or "?" for h in considered]
                err = SmapiError(f"No selectedIntent; considered: {names}")
            else:
                err = SmapiError("No selectedIntent or consideredIntents in response")
            err.considered = considered  # noqa: B010 - attribute contract
            raise err

        intent_name = selected.get("name", "")
        slots: dict[str, dict[str, Any]] = {}
        selected_slots = selected.get("slots")
        if isinstance(selected_slots, dict):
            for slot_name, slot_data in selected_slots.items():
                # One normalization per entry (the parse_nlu_result
                # spelling, JF-936 simplify round): an unusable slot entry
                # reads as empty; keep the two parsers' spelling identical.
                entry = slot_data if isinstance(slot_data, dict) else {}
                slots[slot_name] = {
                    "name": slot_name,
                    "value": entry.get("value", ""),
                }

        return {
            "intent": intent_name,
            "slots": slots,
            "considered": considered,
            "raw": response,
        }

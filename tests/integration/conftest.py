"""Pytest configuration and fixtures for NLU and E2E integration tests.

Provides:
- Automatic skill_id detection from ASK CLI config or environment
- SMAPI rate-limit delay configuration
- YAML fixture loading and parametrized test generation
- --dry-run flag for fixture-only validation
- E2E options: --jellyfin-url, --jellyfin-api-key, --jellyfin-user
- Custom markers: nlu, locale, e2e
- NLU_ROW_FILTER: run only the NLU rows a `nlu_static select` output lists (JF-954.7)
"""

from __future__ import annotations

import json
import logging
import os
import signal
import sys
import warnings
from pathlib import Path
from typing import Any

# Ensure the integration test directory is on sys.path so that
# ``from smapi_client import ...`` works regardless of cwd.
sys.path.insert(0, str(Path(__file__).parent))

# JF-954.5: the nlu_static ledger writer lives under scripts/nlu_static; put
# the repository's scripts directory on sys.path so conftest and test_nlu can
# import the writer and the shared normalizer.
_SCRIPTS_DIR = Path(__file__).resolve().parents[2] / "scripts"
if str(_SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS_DIR))

# Checkpoint/resume support (2026-11-24): inlined below (not a separate
# plugin to avoid double-registration; the logic is small enough to live
# here). See scripts/run_nlu_tests.sh --resume / --fresh.

import pytest
import yaml

# JF-954.5: the results ledger. The bridge is a separate module (the
# smapi_timeouts lesson) so test_nlu.py and this file share ONE holder.
from nlu_static import ledger as nlu_ledger  # noqa: E402
# JF-954.7: the change-driven row selection. The pure decision function and
# the filter-file loader live in scripts/nlu_static/selection.py; the hook
# below only calls them.
from nlu_static import selection as nlu_selection  # noqa: E402
import nlu_ledger_bridge  # noqa: E402

FIXTURES_DIR = Path(__file__).parent / "fixtures"

logger = logging.getLogger("e2e.conftest")


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="session")
def dry_run(request: pytest.FixtureRequest) -> bool:
    """True when --dry-run was passed: no SMAPI subprocess may fire (JF-435)."""
    return bool(request.config.getoption("--dry-run", default=False))


@pytest.fixture(scope="session")
def skill_id(dry_run: bool) -> str:
    """Resolve the Alexa skill ID from ASK CLI config or environment.

    Lookup order:
        1. ``~/.ask/ask_states.json`` -> skillMetadata values
        2. ``ASK_SKILL_ID`` environment variable
        3. Returns ``dry-run-placeholder`` in dry-run mode.

    Raises:
        pytest.FixtureLookupError: when no skill ID can be resolved
        outside of dry-run mode.
    """
    # In dry-run mode we never call SMAPI, so a placeholder is fine.
    if dry_run:
        return "dry-run-placeholder"

    # Attempt auto-detection from ASK CLI state file.
    sid = _resolve_skill_id()
    if sid:
        return sid

    pytest.fail(
        "Cannot resolve skill_id: ~/.ask/ask_states.json not found or "
        "missing skill_id, and ASK_SKILL_ID env var is unset."
    )


@pytest.fixture(scope="session")
def smapi_delay() -> float:
    """Minimum delay (seconds) between SMAPI calls to respect rate limits.

    Controlled via the ``SMAPI_DELAY`` environment variable (default 1.5s).
    """
    return float(os.environ.get("SMAPI_DELAY", "1.5"))


# ---------------------------------------------------------------------------
# Fixture loader helper
# ---------------------------------------------------------------------------


def load_locale_fixtures(
    fixture_dir: Path,
    prefix: str = "",
    exclude_prefixes: tuple[str, ...] = ("e2e_",),
) -> list[dict[str, Any]]:
    """Discover and parse YAML fixture files under *fixture_dir*.

    Args:
        prefix: Only load files starting with this prefix (empty = all).
        exclude_prefixes: Skip files starting with any of these prefixes
            (tuple, because the e2e family now has three sub-families:
            one-shot ``e2e_``, ``e2e_reliability_``, ``e2e_smoke_``).

    Each YAML file has a top-level ``locale``, ``invocation_name``, and
    a ``tests`` list.  This function flattens the structure so each
    individual test case carries its locale and invocation name.  An
    optional top-level ``open_utterance`` (smoke fixtures) is carried
    onto each case the same way.
    """
    fixtures: list[dict[str, Any]] = []
    if not fixture_dir.is_dir():
        return fixtures

    for path in sorted(fixture_dir.glob("*.yaml")):
        name = path.name
        if prefix and not name.startswith(prefix):
            continue
        if any(name.startswith(ex) for ex in exclude_prefixes):
            continue

        with open(path, encoding="utf-8") as handle:
            data = yaml.safe_load(handle)

        if not isinstance(data, dict):
            continue

        locale = data.get("locale", "unknown")
        invocation_name = data.get("invocation_name", "")
        open_utterance = data.get("open_utterance", "")
        test_cases = data.get("tests", [])

        for case in test_cases:
            fixtures.append({
                **case,
                "locale": locale,
                "invocation_name": invocation_name,
                "open_utterance": open_utterance,
                "source": str(path),
            })

    return fixtures


# ---------------------------------------------------------------------------
# pytest hooks
# ---------------------------------------------------------------------------


def pytest_addoption(parser: pytest.Parser) -> None:
    """Register CLI flags for NLU and E2E tests."""
    parser.addoption(
        "--dry-run",
        action="store_true",
        default=False,
        help="Skip SMAPI calls; validate fixture structure only.",
    )
    parser.addoption(
        "--smapi-timeout",
        type=float,
        default=120.0,
        help="Per-test timeout in seconds for live SMAPI calls (default: 120).",
    )
    parser.addoption(
        "--jellyfin-url",
        default=os.environ.get("JELLYFIN_URL", ""),
        help="Jellyfin server base URL (or set JELLYFIN_URL env var).",
    )
    parser.addoption(
        "--jellyfin-api-key",
        default=os.environ.get("JELLYFIN_API_KEY", ""),
        help="Jellyfin API key (or set JELLYFIN_API_KEY env var).",
    )
    parser.addoption(
        "--jellyfin-user",
        default=os.environ.get("JELLYFIN_USER", ""),
        help="Jellyfin user ID for E2E tests (or set JELLYFIN_USER env var).",
    )
    # Checkpoint/resume flags (2026-11-24)
    parser.addoption(
        "--resume", action="store_true", default=False,
        help="Skip tests that already have a result in the checkpoint file.",
    )
    parser.addoption(
        "--fresh", action="store_true", default=False,
        help="Delete the checkpoint file before running (start clean).",
    )


# --- Checkpoint/resume (2026-11-24) ---
_CHECKPOINT_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".nlu_checkpoint.json")


def _resolve_skill_id() -> str | None:
    """The Alexa skill id from ASK CLI state or environment, or None.

    ONE resolution definition, shared by the skill_id fixture and the JF-954.5
    ledger wiring (failing hard is the fixture's concern, continuing with
    nulls is the ledger's; the lookup itself must not fork). Lookup order:
    1. ``~/.ask/ask_states.json`` -> skillMetadata values, 2. ``ASK_SKILL_ID``.
    """
    config_path = os.path.expanduser("~/.ask/ask_states.json")
    if os.path.isfile(config_path):
        try:
            with open(config_path, encoding="utf-8") as handle:
                data = json.load(handle)
            for value in data.get("skillMetadata", {}).values():
                sid = value.get("skill_id", "")
                if sid:
                    return sid
        except (json.JSONDecodeError, OSError, AttributeError):
            pass  # fall through to the environment
    return os.environ.get("ASK_SKILL_ID", "") or None


# --- NLU results ledger (JF-954.5) ---
_ledger_writer: nlu_ledger.LedgerWriter | None = None
# Resolved once in pytest_configure: the session's identity reads reuse it
# instead of re-parsing ask_states.json per call.
_ledger_resolved_skill_id: str | None = None
# Session-wide identity breaker: tripped by an exception (a hung ask CLI) or
# by IDENTITY_NULL_BREAKER_STREAK consecutive null results (a throttled CLI),
# after which every identity read returns null without a new subprocess.
_ledger_identity_broken: bool = False
_ledger_identity_null_streak: int = 0
IDENTITY_NULL_BREAKER_STREAK = 3


def _ledger_identity_reader(locale: str) -> str | None:
    """The live interaction-model eTag for one locale, or None.

    One read-only get-skill-status call per invocation through the existing
    SmapiClient wrapper; the writer calls it at most twice per locale per
    session (locale start and session end). Failures land in the writer's
    non-fatal guard, which records null. The session-wide breaker trips on an
    exception (a hung ask CLI) or after IDENTITY_NULL_BREAKER_STREAK
    consecutive nulls (a throttled CLI), so a dead SMAPI degrades after a
    bounded cost instead of taxing every locale start and the session
    teardown.
    """
    global _ledger_identity_broken, _ledger_identity_null_streak
    if _ledger_identity_broken or not _ledger_resolved_skill_id:
        return None
    try:
        from smapi_client import SmapiClient

        identity = SmapiClient(skill_id=_ledger_resolved_skill_id,
                               locale=locale).live_model_identity()
    except Exception:
        _ledger_identity_broken = True
        logger.warning("NLU ledger identity reads disabled for this session "
                       "after an ask CLI failure")
        return None
    if identity is None:
        _ledger_identity_null_streak += 1
        if _ledger_identity_null_streak >= IDENTITY_NULL_BREAKER_STREAK:
            _ledger_identity_broken = True
            logger.warning(
                "NLU ledger identity reads disabled for this session after "
                "%d consecutive nulls", _ledger_identity_null_streak,
            )
    else:
        _ledger_identity_null_streak = 0
    return identity


def _ledger_glue_fault(exc: Exception) -> None:
    """Report the first glue fault once and disable the session's writer."""
    global _ledger_writer
    warnings.warn(
        nlu_ledger.DISABLED_WARNING_TEMPLATE.format(exc=exc),
        nlu_ledger.NluLedgerWarning,
    )
    if _ledger_writer is not None:
        _ledger_writer.close()
    _ledger_writer = None


def _read_checkpoint() -> dict:
    try:
        with open(_CHECKPOINT_FILE, encoding="utf-8") as fh:
            return json.load(fh)
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def _write_checkpoint(data: dict) -> None:
    tmp = _CHECKPOINT_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2)
    os.rename(tmp, _CHECKPOINT_FILE)


def pytest_configure(config: pytest.Config) -> None:
    """Register custom markers and configure NLU logging."""
    config.addinivalue_line("markers", "nlu: NLU simulation test via SMAPI")
    config.addinivalue_line(
        "markers", "locale(name): locale under test (e.g. en-US, it-IT)"
    )
    config.addinivalue_line(
        "markers", "e2e: full-chain E2E test via SMAPI simulate-skill"
    )
    # Checkpoint: clear on --fresh, report on --resume
    if config.getoption("--fresh"):
        if os.path.exists(_CHECKPOINT_FILE):
            os.remove(_CHECKPOINT_FILE)
    if config.getoption("--resume"):
        cp = _read_checkpoint()
        if cp:
            print(f"[checkpoint] Resuming: {len(cp)} tests already have results")
        else:
            print("[checkpoint] No checkpoint found, running fresh")

    # Configure the nlu.smapi logger so progress is visible with -v/--verbose
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(name)s %(levelname)s %(message)s",
        datefmt="%H:%M:%S",
    )

    # JF-954.5: create the ledger writer once per pytest session. Dry-run
    # writes nothing and NLU_LEDGER=0 disables; the live writer opens its
    # session file here with the skill id resolved once (never raising), and
    # the identity reads reuse the same resolved value.
    global _ledger_writer, _ledger_resolved_skill_id
    writer = nlu_ledger.writer_for_environment(
        dry_run=bool(config.getoption("--dry-run")),
        identity_reader=_ledger_identity_reader,
    )
    _ledger_writer = writer
    _ledger_resolved_skill_id = _resolve_skill_id()
    if writer is not None:
        writer.start_session(skill_id=_ledger_resolved_skill_id)


# ---------------------------------------------------------------------------
# E2E fixtures
# ---------------------------------------------------------------------------


def _jellyfin_configured(request: pytest.FixtureRequest) -> bool:
    """Check whether Jellyfin E2E parameters are all provided."""
    return bool(
        request.config.getoption("--jellyfin-url")
        and request.config.getoption("--jellyfin-api-key")
        and request.config.getoption("--jellyfin-user")
    )


@pytest.fixture(scope="session")
def jellyfin_client(request: pytest.FixtureRequest, dry_run: bool):
    """Create a JellyfinClient if E2E parameters are configured, else None."""
    if dry_run:
        return None

    url = request.config.getoption("--jellyfin-url")
    api_key = request.config.getoption("--jellyfin-api-key")
    user_id = request.config.getoption("--jellyfin-user")

    if not (url and api_key and user_id):
        return None

    from jellyfin_client import JellyfinClient

    client = JellyfinClient(
        base_url=url,
        api_key=api_key,
        user_id=user_id,
    )

    if not client.health_check():
        pytest.exit(
            f"Jellyfin server at {url} is not reachable. "
            "Check --jellyfin-url or JELLYFIN_URL env var.",
            returncode=2,
        )

    return client


@pytest.fixture(autouse=True)
def _skip_without_jellyfin(request: pytest.FixtureRequest, dry_run: bool):
    """Skip E2E tests when Jellyfin parameters are not configured."""
    marker = request.node.get_closest_marker("e2e")
    if marker is None:
        yield
        return

    if dry_run:
        yield
        return

    if not _jellyfin_configured(request):
        pytest.skip(
            "Jellyfin E2E parameters not configured "
            "(--jellyfin-url, --jellyfin-api-key, --jellyfin-user)"
        )
    yield


@pytest.fixture(autouse=True)
def _e2e_cleanup(request: pytest.FixtureRequest, jellyfin_client):
    """Stop playback after each E2E test to avoid state leakage."""
    yield
    marker = request.node.get_closest_marker("e2e")
    if marker is not None and jellyfin_client is not None:
        try:
            jellyfin_client.stop_playback()
        except Exception:  # noqa: BLE001
            logger.debug("E2E cleanup: could not stop playback")


def pytest_collection_modifyitems(
    config: pytest.Config,
    items: list[pytest.Item],
) -> None:
    """Attach a ``locale`` marker; skip completed tests when --resume is set."""
    # Checkpoint: skip items whose nodeid already has a result
    if config.getoption("--resume"):
        checkpoint = _read_checkpoint()
        if checkpoint:
            remaining = []
            skipped_count = 0
            for item in items:
                if item.nodeid in checkpoint:
                    item.add_marker(pytest.mark.skip(
                        reason=f"checkpoint: already {checkpoint[item.nodeid]}"))
                    skipped_count += 1
                else:
                    remaining.append(item)
            if skipped_count:
                print(f"[checkpoint] Skipping {skipped_count} completed, "
                      f"running {len(remaining)} remaining")
            items[:] = remaining

    # Locale markers
    for item in items:
        if not hasattr(item, "callspec"):
            continue
        test_id = item.callspec.id  # type: ignore[union-attr]
        if " - " in test_id:
            locale_name = test_id.split(" - ")[0]
            item.add_marker(pytest.mark.locale(locale_name))

    # JF-954.7: change-driven NLU row selection. When NLU_ROW_FILTER points
    # at a file produced by `nlu_static select`, NLU test items whose
    # (locale, utterance) is not listed are DESELECTED (pytest's own
    # mechanism, so the summary shows the count) and one line states how
    # many rows were kept and from which file. Unset means byte-for-byte
    # the behavior above. A filter file that cannot be read or does not
    # pass the schema check aborts the session: running everything or
    # nothing silently would both be wrong. Non-NLU tests (E2E,
    # reliability, smoke and anything else) are never touched. The
    # checkpoint block above ran first, so --resume keeps working: its
    # already-skipped items left `items` before this filter saw them.
    filter_path = os.environ.get("NLU_ROW_FILTER")
    if filter_path:
        try:
            row_filter = nlu_selection.load_row_filter(filter_path)
        except (OSError, ValueError) as err:
            pytest.exit(
                f"error: NLU_ROW_FILTER {filter_path}: {err}",
                returncode=2,
            )
        nlu_items = [item for item in items if _is_nlu_row_item(item)]
        kept: list[pytest.Item] = []
        dropped: list[pytest.Item] = []
        for item in nlu_items:
            case = item.callspec.params["nlu_fixture"]  # type: ignore[union-attr]
            if nlu_selection.should_keep(
                str(case.get("locale", "")), str(case.get("utterance", "")), row_filter
            ):
                kept.append(item)
            else:
                dropped.append(item)
        if dropped:
            config.hook.pytest_deselected(items=dropped)
            dropped_ids = {id(item) for item in dropped}
            items[:] = [item for item in items if id(item) not in dropped_ids]
        print(
            f"[nlu_row_filter] kept {len(kept)} of {len(nlu_items)} NLU rows "
            f"from {filter_path} ({len(dropped)} deselected)"
        )


def _is_nlu_row_item(item: pytest.Item) -> bool:
    """True for the parametrized NLU rows (the nlu_fixture family only).

    The same family discriminator the ledger writer uses, so the filter and
    the ledger can never disagree about what an NLU row is; e2e_fixture,
    reliability_fixture and smoke_fixture items are different families and
    are never filtered.
    """
    callspec = getattr(item, "callspec", None)
    return callspec is not None and "nlu_fixture" in callspec.params


def pytest_runtest_logreport(report: pytest.TestReport) -> None:
    """Record each test's outcome to the checkpoint file as it completes."""
    if report.when == "call":
        outcome = report.outcome.upper()
        if outcome in ("PASSED", "FAILED"):
            cp = _read_checkpoint()
            cp[report.nodeid] = outcome
            _write_checkpoint(cp)


# ---------------------------------------------------------------------------
# NLU results ledger row recording (JF-954.5)
# ---------------------------------------------------------------------------


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item: pytest.Item, call):
    """Write one ledger row per executed NLU test (outcome as the suite decided).

    Non-call phases only reset the observation holder, so a value from one
    test can never leak into the next row. Any fault in the glue is reported
    once and disables the ledger; it never changes a test outcome.
    """
    outcome = yield
    rep = outcome.get_result()
    try:
        if rep.when != "call":
            nlu_ledger_bridge.reset()
            return
        _ledger_record_row(item, rep)
    except Exception as exc:
        _ledger_glue_fault(exc)


def _ledger_record_row(item: pytest.Item, rep) -> None:
    """Record the ledger row for one finished NLU test, or return silently."""
    writer = _ledger_writer
    if writer is None:
        return
    callspec = getattr(item, "callspec", None)
    if callspec is None or "nlu_fixture" not in callspec.params:
        # E2E, reliability, smoke and non-parametrized tests are out of scope.
        return
    case = callspec.params["nlu_fixture"]
    actual_intent, resolved_slots = nlu_ledger_bridge.take()
    if rep.outcome == "skipped":
        # Skipped rows are not recorded (the task contract).
        return
    if rep.passed:
        row_outcome, failure_kind = "pass", None
        detail = None
    else:
        crash = getattr(rep.longrepr, "reprcrash", None)
        crash_message = getattr(crash, "message", "") or ""
        full_text = rep.longreprtext if rep.longrepr is not None else ""
        if not crash_message and full_text:
            # No crash summary available: the traceback's last non-empty line
            # names the actual failure better than the generic header. The
            # CLASSIFIER still reads the full text so the pinned message
            # fragments are found wherever they sit in the report.
            lines = [ln for ln in full_text.splitlines() if ln.strip()]
            crash_message = lines[-1] if lines else ""
        row_outcome, failure_kind = nlu_ledger.classify_failure(full_text or crash_message)
        detail = crash_message.splitlines()[0][:240] if crash_message else None
    writer.record_row(
        locale=str(case.get("locale", "")),
        utterance=case.get("utterance", ""),
        expected_intent=case.get("expected_intent", ""),
        expected_slots=case.get("expected_slots", {}),
        actual_intent=actual_intent,
        slots=resolved_slots,
        outcome=row_outcome,
        failure_kind=failure_kind,
        detail=detail,
    )


def pytest_sessionfinish(session: pytest.Session, exitstatus: int) -> None:
    """Report checkpoint state and close the ledger session at the end of the run."""
    global _ledger_writer
    writer = _ledger_writer
    if writer is not None:
        try:
            writer.finish()
        except Exception as exc:  # the ledger must never change a test outcome
            _ledger_glue_fault(exc)
        _ledger_writer = None
    if not os.path.exists(_CHECKPOINT_FILE):
        return
    cp = _read_checkpoint()
    passed = sum(1 for v in cp.values() if v == "PASSED")
    failed = sum(1 for v in cp.values() if v == "FAILED")
    total = len(cp)
    print(f"\n[checkpoint] {total} tests recorded: {passed} passed, {failed} failed")
    if exitstatus != 0:
        print(f"[checkpoint] Resume with: pytest --resume (skips the {total} completed)")


def _parametrize_fixtures(
    metafunc: pytest.Metafunc,
    fixture_name: str,
    fixtures: list[dict[str, Any]],
    id_prefix: str = "",
) -> None:
    """Parametrize a test with loaded fixtures and generate test IDs."""
    if not fixtures:
        return
    ids: list[str] = []
    cases: list[dict[str, Any]] = []
    prefix = f"{id_prefix}:" if id_prefix else ""
    for fixture in fixtures:
        locale = fixture.get("locale", "unknown")
        utterance = fixture.get("utterance", "")
        ids.append(f"{prefix}{locale} - {utterance}")
        cases.append(fixture)
    metafunc.parametrize(fixture_name, cases, ids=ids, indirect=True)


def pytest_generate_tests(metafunc: pytest.Metafunc) -> None:
    """Parametrize tests that accept ``nlu_fixture`` or ``e2e_fixture``."""
    if "nlu_fixture" in metafunc.fixturenames:
        _parametrize_fixtures(
            metafunc, "nlu_fixture", load_locale_fixtures(FIXTURES_DIR),
        )

    if "e2e_fixture" in metafunc.fixturenames:
        _parametrize_fixtures(
            metafunc, "e2e_fixture",
            load_locale_fixtures(
                FIXTURES_DIR, prefix="e2e_",
                # The other two e2e sub-families have their own tests;
                # before the smoke family existed, the reliability file
                # leaked here too (each reliability utterance ran twice,
                # once as a one-shot full-chain case).
                exclude_prefixes=("e2e_reliability_", "e2e_smoke_"),
            ),
            id_prefix="e2e",
        )

    if "reliability_fixture" in metafunc.fixturenames:
        _parametrize_fixtures(
            metafunc, "reliability_fixture",
            load_locale_fixtures(
                FIXTURES_DIR, prefix="e2e_reliability_", exclude_prefixes=()
            ),
            id_prefix="reliability",
        )

    if "smoke_fixture" in metafunc.fixturenames:
        _parametrize_fixtures(
            metafunc, "smoke_fixture",
            load_locale_fixtures(
                FIXTURES_DIR, prefix="e2e_smoke_", exclude_prefixes=()
            ),
            id_prefix="smoke",
        )


# ---------------------------------------------------------------------------
# Per-test timeout for live SMAPI tests
# ---------------------------------------------------------------------------

# JF-897: the exception class lives in the shared smapi_timeouts module so
# conftest and test_e2e import the SAME object regardless of how pytest
# resolves conftest's module identity (a rootdir/sys.path change would
# otherwise make the except clause in test_e2e silently miss the alarm).
from smapi_timeouts import _TimeoutError, _timeout_handler  # noqa: E402

# Checkpoint/resume support (2026-11-24): --resume/--fresh flags


@pytest.fixture(autouse=True)
def _smapi_test_timeout(request: pytest.FixtureRequest, dry_run: bool) -> Any:
    """Apply a per-test timeout to NLU and E2E tests to prevent indefinite hangs.

    Only active for tests marked with @pytest.mark.nlu or @pytest.mark.e2e
    and not in dry-run mode.
    Uses SIGALRM on Linux/macOS. Controlled by --smapi-timeout CLI option.
    """
    marker = request.node.get_closest_marker("nlu")
    e2e_marker = request.node.get_closest_marker("e2e")
    if marker is None and e2e_marker is None:
        yield
        return

    if dry_run:
        yield
        return

    timeout = request.config.getoption("--smapi-timeout", default=120.0) or 120.0
    old_handler = signal.signal(signal.SIGALRM, _timeout_handler)
    signal.alarm(int(timeout))
    try:
        yield
    except _TimeoutError:
        pytest.fail(
            f"Test timed out after {timeout:.0f}s — SMAPI likely rate-limited or "
            f"simulation stuck. Try increasing --smapi-timeout or adding a delay "
            f"between runs with SMAPI_DELAY env var."
        )
    finally:
        signal.alarm(0)
        signal.signal(signal.SIGALRM, old_handler)

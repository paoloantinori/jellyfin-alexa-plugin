"""Pytest configuration and fixtures for NLU integration tests.

Provides:
- Automatic skill_id detection from ASK CLI config or environment
- SMAPI rate-limit delay configuration
- YAML fixture loading and parametrized test generation
- --dry-run flag for fixture-only validation
- Custom markers: nlu, locale
"""

from __future__ import annotations

import json
import logging
import os
import signal
import sys
from pathlib import Path
from typing import Any

# Ensure the integration test directory is on sys.path so that
# ``from smapi_client import ...`` works regardless of cwd.
sys.path.insert(0, str(Path(__file__).parent))

import pytest
import yaml

FIXTURES_DIR = Path(__file__).parent / "fixtures"


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="session")
def skill_id(request: pytest.FixtureRequest) -> str:
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
    if request.config.getoption("--dry-run", default=False):
        return "dry-run-placeholder"

    # Attempt auto-detection from ASK CLI state file.
    config_path = os.path.expanduser("~/.ask/ask_states.json")
    if os.path.isfile(config_path):
        try:
            with open(config_path, encoding="utf-8") as handle:
                data = json.load(handle)
            for value in data.get("skillMetadata", {}).values():
                sid = value.get("skill_id", "")
                if sid:
                    return sid
        except (json.JSONDecodeError, OSError):
            pass  # fall through to env var

    env_id = os.environ.get("ASK_SKILL_ID", "")
    if env_id:
        return env_id

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


def load_locale_fixtures(fixture_dir: Path) -> list[dict[str, Any]]:
    """Discover and parse all YAML fixture files under *fixture_dir*.

    Each YAML file has a top-level ``locale``, ``invocation_name``, and
    a ``tests`` list.  This function flattens the structure so each
    individual test case carries its locale and invocation name.

    Returns:
        A list of dicts, one per utterance, each augmented with
        ``locale``, ``invocation_name``, and ``source`` keys.
    """
    fixtures: list[dict[str, Any]] = []
    if not fixture_dir.is_dir():
        return fixtures

    for path in sorted(fixture_dir.glob("*.yaml")):
        with open(path, encoding="utf-8") as handle:
            data = yaml.safe_load(handle)

        if not isinstance(data, dict):
            continue

        locale = data.get("locale", "unknown")
        invocation_name = data.get("invocation_name", "")
        test_cases = data.get("tests", [])

        for case in test_cases:
            fixtures.append({
                **case,
                "locale": locale,
                "invocation_name": invocation_name,
                "source": str(path),
            })

    return fixtures


# ---------------------------------------------------------------------------
# pytest hooks
# ---------------------------------------------------------------------------


def pytest_addoption(parser: pytest.Parser) -> None:
    """Register the --dry-run CLI flag."""
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


def pytest_configure(config: pytest.Config) -> None:
    """Register custom markers and configure NLU logging."""
    config.addinivalue_line("markers", "nlu: NLU simulation test via SMAPI")
    config.addinivalue_line(
        "markers", "locale(name): locale under test (e.g. en-US, it-IT)"
    )

    # Configure the nlu.smapi logger so progress is visible with -v/--verbose
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(name)s %(levelname)s %(message)s",
        datefmt="%H:%M:%S",
    )


def pytest_collection_modifyitems(
    config: pytest.Config,  # noqa: ARG001 – required by hookspec
    items: list[pytest.Item],
) -> None:
    """Attach a ``locale`` marker to each test based on its parametrize id."""
    for item in items:
        if not hasattr(item, "callspec"):
            continue
        test_id = item.callspec.id  # type: ignore[union-attr]
        if " - " in test_id:
            locale_name = test_id.split(" - ")[0]
            item.add_marker(pytest.mark.locale(locale_name))


def pytest_generate_tests(metafunc: pytest.Metafunc) -> None:
    """Parametrize tests that accept the ``nlu_fixture`` indirect parameter.

    Discovers YAML files from the fixtures directory and generates one
    test case per utterance/locale combination.
    """
    if "nlu_fixture" not in metafunc.fixturenames:
        return

    fixture_root = FIXTURES_DIR
    fixtures = load_locale_fixtures(fixture_root)

    if not fixtures:
        return

    ids: list[str] = []
    cases: list[dict[str, Any]] = []
    for fixture in fixtures:
        locale = fixture.get("locale", "unknown")
        utterance = fixture.get("utterance", "")
        test_id = f"{locale} - {utterance}"
        ids.append(test_id)
        cases.append(fixture)

    metafunc.parametrize("nlu_fixture", cases, ids=ids, indirect=True)


# ---------------------------------------------------------------------------
# Per-test timeout for live SMAPI tests
# ---------------------------------------------------------------------------

class _TimeoutError(Exception):
    """Raised when a test exceeds its SMAPI timeout."""


def _timeout_handler(signum: int, frame: Any) -> None:  # noqa: ARG001
    raise _TimeoutError("Test exceeded per-test SMAPI timeout")


@pytest.fixture(autouse=True)
def _smapi_test_timeout(request: pytest.FixtureRequest) -> Any:
    """Apply a per-test timeout to NLU tests to prevent indefinite hangs.

    Only active for tests marked with @pytest.mark.nlu and not in dry-run mode.
    Uses SIGALRM on Linux/macOS. Controlled by --smapi-timeout CLI option.
    """
    marker = request.node.get_closest_marker("nlu")
    if marker is None:
        yield
        return

    if request.config.getoption("--dry-run", default=False):
        yield
        return

    timeout = request.config.getoption("--smapi-timeout", default=120.0)
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

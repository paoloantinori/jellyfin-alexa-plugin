"""Pytest configuration and fixtures for NLU and E2E integration tests.

Provides:
- Automatic skill_id detection from ASK CLI config or environment
- SMAPI rate-limit delay configuration
- YAML fixture loading and parametrized test generation
- --dry-run flag for fixture-only validation
- E2E options: --jellyfin-url, --jellyfin-api-key, --jellyfin-user
- Custom markers: nlu, locale, e2e
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

logger = logging.getLogger("e2e.conftest")


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


def load_locale_fixtures(fixture_dir: Path,
                         prefix: str = "",
                         exclude_prefix: str = "e2e_") -> list[dict[str, Any]]:
    """Discover and parse YAML fixture files under *fixture_dir*.

    Args:
        prefix: Only load files starting with this prefix (empty = all).
        exclude_prefix: Skip files starting with this prefix.

    Each YAML file has a top-level ``locale``, ``invocation_name``, and
    a ``tests`` list.  This function flattens the structure so each
    individual test case carries its locale and invocation name.
    """
    fixtures: list[dict[str, Any]] = []
    if not fixture_dir.is_dir():
        return fixtures

    for path in sorted(fixture_dir.glob("*.yaml")):
        name = path.name
        if prefix and not name.startswith(prefix):
            continue
        if exclude_prefix and name.startswith(exclude_prefix):
            continue

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


def pytest_configure(config: pytest.Config) -> None:
    """Register custom markers and configure NLU logging."""
    config.addinivalue_line("markers", "nlu: NLU simulation test via SMAPI")
    config.addinivalue_line(
        "markers", "locale(name): locale under test (e.g. en-US, it-IT)"
    )
    config.addinivalue_line(
        "markers", "e2e: full-chain E2E test via SMAPI simulate-skill"
    )

    # Configure the nlu.smapi logger so progress is visible with -v/--verbose
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(name)s %(levelname)s %(message)s",
        datefmt="%H:%M:%S",
    )


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
def jellyfin_client(request: pytest.FixtureRequest):
    """Create a JellyfinClient if E2E parameters are configured, else None."""
    if request.config.getoption("--dry-run"):
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
def _skip_without_jellyfin(request: pytest.FixtureRequest):
    """Skip E2E tests when Jellyfin parameters are not configured."""
    marker = request.node.get_closest_marker("e2e")
    if marker is None:
        yield
        return

    if request.config.getoption("--dry-run"):
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
            load_locale_fixtures(FIXTURES_DIR, prefix="e2e_", exclude_prefix=""),
            id_prefix="e2e",
        )


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

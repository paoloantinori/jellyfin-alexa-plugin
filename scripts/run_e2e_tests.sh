#!/usr/bin/env bash
# Run E2E integration tests for the Jellyfin Alexa skill plugin.
#
# Exercises the full chain: Alexa utterance -> NLU -> skill endpoint -> Jellyfin API.
# Requires a running Jellyfin server accessible to the Alexa skill endpoint.
#
# Usage:
#   ./scripts/run_e2e_tests.sh                           # run all E2E tests
#   ./scripts/run_e2e_tests.sh --dry-run                 # validate fixtures only
#   ./scripts/run_e2e_tests.sh -k "PlayMoodMusic"        # filter by intent
#
# Jellyfin connection (any of):
#   CLI flags:  --jellyfin-url https://... --jellyfin-api-key ... --jellyfin-user ...
#   Env vars:   JELLYFIN_URL, JELLYFIN_API_KEY, JELLYFIN_USER
#
# Without Jellyfin connection params, E2E tests are automatically skipped.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INTEGRATION_DIR="${SCRIPT_DIR}/../tests/integration"
VENV_DIR="${INTEGRATION_DIR}/.venv"

# Set up venv if needed
if [ ! -f "${VENV_DIR}/bin/python" ]; then
    echo "Creating virtual environment..."
    python3 -m venv "${VENV_DIR}"
fi

# Install dependencies
echo "Installing dependencies..."
"${VENV_DIR}/bin/pip" install -q -r "${INTEGRATION_DIR}/requirements.txt"

# Run pytest.  The hermetic unit modules ride along so their contracts
# (the JF-899 FAILED-simulation classifier, the JF-893/JF-897 timeout
# verdict, the JF-900 fr fallback composition) are exercised by the
# standard entry point, not only manually.
cd "${INTEGRATION_DIR}"
exec "${VENV_DIR}/bin/python" -m pytest test_e2e.py \
    test_smapi_failure_classification.py \
    test_reliability_timeout_outcome.py \
    test_fallback_one_shot_composition.py \
    --tb=short \
    -v \
    "$@"

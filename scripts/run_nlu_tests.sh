#!/usr/bin/env bash
# Run NLU integration tests using ASK CLI simulate-skill.
#
# Usage:
#   ./scripts/run_nlu_tests.sh              # run all locales
#   ./scripts/run_nlu_tests.sh --dry-run    # validate fixtures only (no SMAPI calls)
#   ./scripts/run_nlu_tests.sh -k "en-US"   # run only en-US tests
#
# Environment variables:
#   ASK_SKILL_ID   Override auto-detected skill ID
#   SMAPI_DELAY    Seconds between SMAPI calls (default: 1.5)
#   PYTEST_ARGS    Extra pytest arguments

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
TEST_DIR="${PROJECT_ROOT}/tests/integration"
VENV_DIR="${TEST_DIR}/.venv"

echo "=== NLU Integration Test Suite ==="

# Create virtual environment if needed
if [ ! -f "${VENV_DIR}/bin/python" ]; then
  echo "Creating virtual environment..."
  python3 -m venv "${VENV_DIR}"
fi

# Install dependencies
echo "Installing dependencies..."
"${VENV_DIR}/bin/pip" install -q -r "${TEST_DIR}/requirements.txt"

# Run pytest from the integration test directory
echo ""
cd "${TEST_DIR}"
exec "${VENV_DIR}/bin/python" -m pytest \
  --rootdir="${TEST_DIR}" \
  --tb=short \
  -v \
  ${PYTEST_ARGS:-} \
  "$@"

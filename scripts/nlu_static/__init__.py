"""nlu_static: deterministic static analysis for the Jellyfin Alexa interaction models.

This is the JF-954 foundation package. Every JF-954 subtask imports the
shared normalizer (``nlu_static.normalize``), the effective-model builder and
snapshot loader (``nlu_static.effective_model``), and the name-level snapshot
producer (``nlu_static.producer_jellyfin``) from here, and adds its own
subcommand to ``nlu_static.cli``.

The package name uses an underscore, not a hyphen, so it imports directly
once the repository's ``scripts/`` directory is on ``sys.path``. Test files
use the two-line bootstrap the existing script tests already use; the CLI is
runnable as ``python3 scripts/nlu_static/cli.py <subcommand>``.

These tools detect necessary conditions for NLU breaks and classify failures
deterministically. They do not predict how Amazon's trainer routes an
utterance; live routing still needs profile-nlu, simulate-skill or a device.
"""

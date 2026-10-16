"""Shared per-test SMAPI timeout primitives (JF-897).

``conftest`` installs the SIGALRM handler; ``test_e2e`` catches the raised
exception. Both must see the SAME class object, so it lives in this plain
module instead of conftest: a rootdir/sys.path change could make pytest
import conftest under a different module identity, and a
``from conftest import _TimeoutError`` except clause would then silently
miss every alarm.
"""

from __future__ import annotations

from typing import Any


class _TimeoutError(Exception):
    """Raised when a test exceeds its SMAPI timeout."""


def _timeout_handler(signum: int, frame: Any) -> None:  # noqa: ARG001
    raise _TimeoutError("Test exceeded per-test SMAPI timeout")

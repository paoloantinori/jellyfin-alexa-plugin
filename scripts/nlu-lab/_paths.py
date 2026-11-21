"""sys.path bootstrap so the lab can import the repo's existing scripts."""
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

def bootstrap() -> None:
    for p in (REPO / "scripts", REPO / "tests" / "integration", Path(__file__).resolve().parent):
        s = str(p)
        if s not in sys.path:
            sys.path.insert(0, s)

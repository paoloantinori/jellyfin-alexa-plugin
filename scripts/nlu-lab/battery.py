"""Battery assembly: the flattened (utterance, source, expected_intent) row list the run
command feeds to profile-nlu. Two sources per locale: the locale's NLU fixture file
(fixtures/<locale>.yaml, the e2e_* family excluded by the exact stem match in
load_battery, mirroring load_locale_fixtures in tests/integration/conftest.py) and an
optional workingset file in the SAME fixture format (top-level locale/invocation_name,
a tests list) where expected_intent is OPTIONAL. A row without expected_intent is an
observation row (expected_intent stays None; the judgment layer treats it amber-only).
Rows are keyed by (utterance, source): the same utterance in both sources stays
distinct. expected_intent is carried verbatim, including "A|B" pipe alternatives, which
the run schema splits at judgment time."""
from dataclasses import dataclass
from pathlib import Path

import yaml

SOURCE_FIXTURE = "fixture"
SOURCE_WORKINGSET = "workingset"


class WorkingsetRefused(ValueError):
    """The workingset file cannot join the battery: missing, unparseable YAML,
    a non-mapping top level, or a locale differing from the run locale. Raised
    by load_battery (the one place the workingset is touched) so the CLI layer
    can convert it into its preflight refusal with the message verbatim."""


@dataclass
class BatteryRow:
    utterance: str
    source: str  # SOURCE_FIXTURE | SOURCE_WORKINGSET
    expected_intent: str | None  # None = observation row; may carry "A|B" alternatives


def load_battery(locale: str, fixtures_dir: Path, workingset: Path | None,
                 filter_k: str | None) -> list[BatteryRow]:
    """Load the battery for one locale; filter_k is a substring filter applied to utterances
    across both sources (None or empty = keep all)."""
    rows: list[BatteryRow] = []

    fixtures_dir = Path(fixtures_dir)
    if fixtures_dir.is_dir():
        for path in sorted(fixtures_dir.glob("*.yaml")):
            # exact stem match: e2e_smoke_<locale>.yaml (and every other
            # prefixed file) never equals the locale, which is what keeps the
            # e2e_* family out of the battery
            if path.stem != locale:
                continue
            data = yaml.safe_load(path.read_text(encoding="utf-8"))
            if not isinstance(data, dict):
                continue
            for case in data.get("tests") or []:
                rows.append(BatteryRow(
                    utterance=case["utterance"],
                    source=SOURCE_FIXTURE,
                    expected_intent=case.get("expected_intent"),
                ))

    if workingset is not None:
        ws_path = Path(workingset)
        if not ws_path.is_file():
            raise WorkingsetRefused(f"--workingset {ws_path}: no such file")
        try:
            data = yaml.safe_load(ws_path.read_text(encoding="utf-8"))
        except yaml.YAMLError as exc:
            raise WorkingsetRefused(
                f"workingset {ws_path} is not parseable YAML: {exc}") from exc
        if not isinstance(data, dict):
            raise WorkingsetRefused(
                f"workingset {ws_path}: top level is {type(data).__name__}, "
                "expected a mapping")
        ws_locale = data.get("locale")
        if ws_locale != locale:
            raise WorkingsetRefused(
                f"workingset {ws_path} declares locale {ws_locale!r} but the run "
                f"locale is {locale!r}; refusing to feed another locale's rows "
                "to profile-nlu")
        for case in data.get("tests") or []:
            rows.append(BatteryRow(
                utterance=case["utterance"],
                source=SOURCE_WORKINGSET,
                expected_intent=case.get("expected_intent"),
            ))

    if filter_k:
        rows = [r for r in rows if filter_k in r.utterance]
    return rows

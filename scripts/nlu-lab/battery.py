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
    """A battery input cannot join the battery: a workingset file that is
    missing, unparseable YAML, not valid UTF-8, a non-mapping top level, or a
    locale differing
    from the run locale; or a tests[] row from EITHER source (fixture or
    workingset) that is not a mapping or lacks the utterance key (JF-935).
    Raised by load_battery (the one place the workingset and the fixture rows
    are touched) so the CLI layer can convert it into its preflight refusal
    with the message verbatim."""


@dataclass
class BatteryRow:
    utterance: str
    source: str  # SOURCE_FIXTURE | SOURCE_WORKINGSET
    expected_intent: str | None  # None = observation row; may carry "A|B" alternatives


def _battery_row(case, path: Path, source: str, index: int) -> BatteryRow:
    """Shape-check one tests[] row and build the BatteryRow. A row that is not a
    mapping (a bare YAML list item) or a mapping without the utterance key (the
    classic typo'd 'utterrence') used to crash load_battery with TypeError /
    KeyError, escaping the CLI's except (RunRefused, LabConfigError) as a raw
    traceback (JF-935); both shapes refuse with the file and the 1-based row
    index instead. A PRESENT utterance with a None or empty value still reaches
    the run preflight's None/empty refusal, unchanged, with or without a -k
    filter (non-string utterances bypass the filter; see the filter comment
    below); that gate now runs on BOTH the lab and the --live paths. A
    non-string expected_intent (the YAML list written instead of the
    documented 'A|B' pipe string) also refuses here: it used to crash the
    run REPORT with an AttributeError after the results were already saved
    (review round 2026-11-24)."""
    if not isinstance(case, dict):
        raise WorkingsetRefused(
            f"{source} {path} row #{index + 1} is {type(case).__name__}, "
            "expected a mapping with an utterance key")
    if "utterance" not in case:
        raise WorkingsetRefused(
            f"{source} {path} row #{index + 1} carries keys {list(case)} but "
            "no utterance; every battery row needs an utterance key")
    expected_intent = case.get("expected_intent")
    if expected_intent is not None and not isinstance(expected_intent, str):
        raise WorkingsetRefused(
            f"{source} {path} row #{index + 1} expected_intent is "
            f"{type(expected_intent).__name__}, expected a string (alternatives "
            "are the documented 'A|B' pipe form); fix the row")
    return BatteryRow(
        utterance=case["utterance"],
        source=source,
        expected_intent=expected_intent,
    )


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
            # JF-956: the fixture read wraps parse and decode failures the
            # same way the workingset twin below does (yaml.YAMLError, plus
            # UnicodeDecodeError: the strict utf-8 decode runs before yaml
            # sees any bytes, so a non-UTF-8 file raised raw before the wrap).
            try:
                data = yaml.safe_load(path.read_text(encoding="utf-8"))
            except yaml.YAMLError as exc:
                raise WorkingsetRefused(
                    f"fixture {path} is not parseable YAML: {exc}") from exc
            except UnicodeDecodeError as exc:
                raise WorkingsetRefused(
                    f"fixture {path} is not valid UTF-8: {exc}") from exc
            if not isinstance(data, dict):
                # A fixture file whose YAML top level is not a mapping (a
                # mangled paste, a top-level list, an empty file parsing to
                # None) must REFUSE, not silently skip: the battery would run
                # without the locale's fixture corpus and report ok on a
                # truncated comparison (review round 2026-11-24; the workingset
                # twin of the identical shape already refused).
                raise WorkingsetRefused(
                    f"fixture {path} top level is "
                    f"{type(data).__name__ if data is not None else 'empty'}, "
                    "expected a mapping with locale/invocation_name/tests")
            for i, case in enumerate(data.get("tests") or []):
                rows.append(_battery_row(case, path, SOURCE_FIXTURE, i))

    if workingset is not None:
        ws_path = Path(workingset)
        if not ws_path.is_file():
            raise WorkingsetRefused(f"--workingset {ws_path}: no such file")
        try:
            data = yaml.safe_load(ws_path.read_text(encoding="utf-8"))
        except yaml.YAMLError as exc:
            raise WorkingsetRefused(
                f"workingset {ws_path} is not parseable YAML: {exc}") from exc
        except UnicodeDecodeError as exc:
            raise WorkingsetRefused(
                f"workingset {ws_path} is not valid UTF-8: {exc}") from exc
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
        for i, case in enumerate(data.get("tests") or []):
            rows.append(_battery_row(case, ws_path, SOURCE_WORKINGSET, i))

    if filter_k:
        # Non-string utterances BYPASS the filter so the run preflight's
        # None/empty refusal (the non-live path) still sees them; the bare
        # membership test TypeError'd on a None utterance whenever -k was
        # set (JF-935 review finding).
        rows = [r for r in rows
                if not isinstance(r.utterance, str) or filter_k in r.utterance]
    return rows

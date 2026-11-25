"""JF-971: the ER catalog canary battery.

Covers, all offline (no SMAPI, no ask CLI, no network): the probe planner
(value selection per wired type, slot-final preference, the not-probeable
markings), the runner (ER verdicts, no-selection, probe errors, SMAPI
pacing), the block/entry helpers, the environment plumbing (NLU_CANARY=0
and the no-snapshot not-run paths, both explicit), the ledger record
emission (start and end, the unwired reader, the stats exclusion rules and
their visible counter) and the triage CATALOG_STATE verdict with its
precedence pins. The lab-side record fields are covered in
scripts/nlu-lab/tests (test_runs.py round-trip, test_cli_run.py --live).
"""

import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
INTEGRATION_DIR = REPO / "tests" / "integration"
for entry in (str(SCRIPTS_DIR), str(INTEGRATION_DIR)):
    if entry not in sys.path:
        sys.path.insert(0, entry)

from nlu_static import catalog_canary  # noqa: E402
from nlu_static import ledger as nlu_ledger  # noqa: E402
from nlu_static import triage as nlu_triage  # noqa: E402

FIXED_MOMENT = datetime(2026, 11, 24, 12, 0, 0, tzinfo=timezone.utc)


# ---------------------------------------------------------------- models


def _write_model(out_dir: Path, locale: str = "xx-XX") -> Path:
    """A tiny model with one catalog type per probe shape.

    SeriesName carries both a mid-sample and a slot-final clean sample
    (slot-final must win); AudiobookTitle only a mid-sample clean one;
    MovieTitle has NO clean sample (two-slot samples only); StaticThing is
    a STATIC type that must never enter a plan.
    """
    model = {
        "languageModel": {
            "invocationName": "test col",
            "intents": [
                {
                    "name": "PlayEpisodeIntent",
                    "slots": [{"name": "series_name", "type": "SeriesName"}],
                    "samples": [
                        "reproduce {series_name} temporada cinco",
                        "sigue con {series_name}",
                    ],
                },
                {
                    "name": "PlayBookIntent",
                    "slots": [{"name": "book", "type": "AudiobookTitle"}],
                    "samples": ["el libro de {book}"],
                },
                {
                    "name": "PlayMovieIntent",
                    "slots": [
                        {"name": "movie", "type": "MovieTitle"},
                        {"name": "year", "type": "AMAZON.NUMBER"},
                    ],
                    "samples": [
                        "la pelicula {movie} de {year}",
                        "el film {movie} ano {year}",
                    ],
                },
                {
                    "name": "StaticIntent",
                    "slots": [{"name": "thing", "type": "StaticThing"}],
                    "samples": ["dime {thing}"],
                },
            ],
            "types": [
                {"name": "SeriesName",
                 "values": [{"name": {"value": "Breaking Bad", "synonyms": []}}]},
                {"name": "StaticThing",
                 "values": [{"name": {"value": "la cosa", "synonyms": []}}]},
            ],
        }
    }
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"model_{locale}.json"
    path.write_text(json.dumps(model), encoding="utf-8")
    return path


def _snapshot(types: dict[str, str] | None = None, *, locale: str = "xx-XX") -> dict:
    """A minimal snapshot; a None types dict means the locale table is absent."""
    if types is None:
        return {"schema": 1, "locales": {"other-LOCALE": {}}}
    table = {
        name: [{"value": value, "synonyms": []}] for name, value in types.items()
    }
    return {"schema": 1, "locales": {locale: table}}


@pytest.fixture()
def models_dir(tmp_path):
    directory = tmp_path / "models"
    _write_model(directory)
    return directory


# ------------------------------------------------------- probe planning


def test_plan_picks_one_value_per_wired_catalog_type(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot({"SeriesName": "Breaking Bad", "AudiobookTitle": "Dune"}),
        models_dir=models_dir,
    )
    by_type = {probe["type"]: probe for probe in plan}
    assert by_type["SeriesName"]["value"] == "Breaking Bad"
    assert by_type["SeriesName"]["intent"] == "PlayEpisodeIntent"
    assert by_type["SeriesName"]["slot"] == "series_name"
    assert by_type["AudiobookTitle"]["value"] == "Dune"
    assert by_type["AudiobookTitle"]["utterance"] == "el libro de Dune"
    # the wired catalog types are exactly the ones the model declares
    assert set(by_type) == {"SeriesName", "AudiobookTitle", "MovieTitle"}


def test_plan_prefers_the_slot_final_sample(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot({"SeriesName": "Breaking Bad"}), models_dir=models_dir
    )
    (series,) = [probe for probe in plan if probe["type"] == "SeriesName"]
    assert series["utterance"] == "sigue con Breaking Bad"


def test_plan_marks_no_clean_sample_not_probeable(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot({"MovieTitle": "Blade Runner"}), models_dir=models_dir
    )
    (movie,) = [probe for probe in plan if probe["type"] == "MovieTitle"]
    assert movie["probeable"] is False
    assert "no committed sample" in movie["reason"]


def test_plan_marks_missing_snapshot_values_not_probeable(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX",
        _snapshot({"SeriesName": "Breaking Bad"}),  # AudiobookTitle absent
        models_dir=models_dir,
    )
    (book,) = [probe for probe in plan if probe["type"] == "AudiobookTitle"]
    assert book["probeable"] is False
    assert book["reason"] == "no snapshot value available"


def test_plan_excludes_static_types(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot({"StaticThing": "la cosa"}), models_dir=models_dir
    )
    # the STATIC type is not wired catalog vocabulary: it never gets a probe
    assert not any(probe.get("type") == "StaticThing" for probe in plan)


def test_plan_locale_without_snapshot_table_marks_everything_not_probeable(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot(None), models_dir=models_dir
    )
    assert plan and all(probe.get("probeable") is False for probe in plan)
    assert all(probe["reason"] == "no snapshot value available" for probe in plan)


def test_real_it_locale_plans_all_six_wired_types():
    """The committed it-IT model wires all six catalog types; every one
    gets a clean single-slot sample to carry the snapshot value."""
    snapshot = {"schema": 1, "locales": {"*": {
        name: [{"value": f"valore {name}", "synonyms": []}]
        for name in ("AlbumName", "AudiobookTitle", "JellyfinArtist",
                     "MovieTitle", "SeriesName", "SongTitle")
    }}}
    plan = catalog_canary.build_canary_plan("it-IT", snapshot)
    assert {probe["type"] for probe in plan} == {
        "AlbumName", "AudiobookTitle", "JellyfinArtist",
        "MovieTitle", "SeriesName", "SongTitle",
    }
    for probe in plan:
        assert probe.get("probeable", True) is True
        assert probe["utterance"].endswith(probe["value"])
        assert "{" not in probe["utterance"]


# ------------------------------------------------------- the runner


def _ok_response(intent: str, slot: str, canonical: str, *, matched: bool = True):
    """A raw profile-nlu response: the intent selected with ER on the slot."""
    authority_record = {
        "status": {"code": "ER_SUCCESS_MATCH" if matched else "ER_SUCCESS_NO_MATCH"},
        "authority": f"AMAZON.{slot}",
        "values": [{"value": {"name": canonical}}] if matched else [],
    }
    slot_obj = {"value": canonical,
                "resolutions": {"resolutionsPerAuthority": [authority_record]}}
    return {
        "selectedIntent": {"name": intent, "slots": {slot: slot_obj}},
        "consideredIntents": [{"name": intent, "slots": {slot: slot_obj}}],
    }


def test_run_canary_true_on_er_match(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot({"SeriesName": "Breaking Bad"}), models_dir=models_dir
    )
    seen = []
    block = catalog_canary.run_canary(
        plan, lambda u: seen.append(u) or _ok_response("PlayEpisodeIntent",
                                                       "series_name", "Breaking Bad"),
        delay=0.0,
    )
    assert block["status"] == "ok"
    record = block["types"]["SeriesName"]
    assert record["resolved"] is True
    assert record["code"] == "ER_SUCCESS_MATCH"
    assert record["authority"] == "AMAZON.series_name"
    assert seen == ["sigue con Breaking Bad"]


def test_run_canary_true_with_the_real_extractor(models_dir):
    """The default extractor path: considered_hypotheses from the ONE
    module-level implementation parses the fake response the same way."""
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot({"SeriesName": "Breaking Bad"}), models_dir=models_dir
    )
    block = catalog_canary.run_canary(
        plan,
        lambda u: _ok_response("PlayEpisodeIntent", "series_name", "Breaking Bad"),
        delay=0.0,
    )
    assert block["types"]["SeriesName"]["resolved"] is True


def test_run_canary_false_on_no_selection(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot({"SeriesName": "Ghost_value"}), models_dir=models_dir
    )
    # JF-684: a catalog value matching nothing selects NO intent at all.
    block = catalog_canary.run_canary(plan, lambda u: {"selectedIntent": None}, delay=0.0)
    record = block["types"]["SeriesName"]
    assert record["resolved"] is False
    assert record["code"] is None and record["authority"] is None


def test_run_canary_false_on_er_mismatch_carries_the_code(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot({"SeriesName": "Ghost_value"}), models_dir=models_dir
    )
    block = catalog_canary.run_canary(
        plan,
        lambda u: _ok_response("PlayEpisodeIntent", "series_name", "x", matched=False),
        delay=0.0,
    )
    record = block["types"]["SeriesName"]
    assert record["resolved"] is False
    assert record["code"] == "ER_SUCCESS_NO_MATCH"


def test_run_canary_true_when_a_later_hypothesis_carries_the_match():
    """The probe intent can appear MULTIPLE times with different slot
    segmentations; a first hypothesis without ER must not mask a later
    matching one (code-review fix)."""
    no_match_slot = {"value": "brano rosanna",
                     "resolutions": {"resolutionsPerAuthority": [
                         {"status": {"code": "ER_SUCCESS_NO_MATCH"},
                          "authority": "AMAZON.librarySong", "values": []}]}}
    match_slot = {"value": "rosanna",
                  "resolutions": {"resolutionsPerAuthority": [
                      {"status": {"code": "ER_SUCCESS_MATCH"},
                       "authority": "AMAZON.SongTitle",
                       "values": [{"value": {"name": "rosanna"}}]}]}}
    response = {
        "selectedIntent": {"name": "PlayLibrarySongIntent",
                           "slots": {"librarySong": match_slot}},
        "consideredIntents": [
            {"name": "PlayLibrarySongIntent", "slots": {"librarySong": no_match_slot}},
            {"name": "PlayLibrarySongIntent", "slots": {"librarySong": match_slot}},
        ],
    }
    verdict = catalog_canary._evaluate_probe(
        response, "PlayLibrarySongIntent", "librarySong",
        catalog_canary._default_hypotheses,
    )
    assert verdict["resolved"] is True
    assert verdict["authority"] == "AMAZON.SongTitle"


def test_run_canary_probe_error_marks_the_type_unknown(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX",
        _snapshot({"SeriesName": "a", "AudiobookTitle": "b"}),
        models_dir=models_dir,
    )

    def profile_fn(utterance):
        if "sigue" in utterance:
            raise RuntimeError("cli hung")
        return _ok_response("PlayBookIntent", "book", "b")

    block = catalog_canary.run_canary(plan, profile_fn, delay=0.0)
    assert block["types"]["SeriesName"]["resolved"] is None
    assert "RuntimeError" in block["types"]["SeriesName"]["error"]
    assert block["types"]["AudiobookTitle"]["resolved"] is True


def test_run_canary_paces_between_probes(models_dir, monkeypatch):
    plan = catalog_canary.build_canary_plan(
        "xx-XX",
        _snapshot({"SeriesName": "a", "AudiobookTitle": "b"}),
        models_dir=models_dir,
    )
    sleeps = []
    monkeypatch.setattr(catalog_canary.time, "sleep", sleeps.append)
    catalog_canary.run_canary(plan, lambda u: {}, delay=2.5)
    # two probes, one inter-probe sleep; the first call is never delayed
    assert sleeps == [2.5]


def test_run_canary_carries_not_probeable_entries(models_dir):
    plan = catalog_canary.build_canary_plan(
        "xx-XX", _snapshot({"MovieTitle": "x"}), models_dir=models_dir
    )
    block = catalog_canary.run_canary(plan, lambda u: {}, delay=0.0)
    assert block["types"]["MovieTitle"] == {
        "probeable": False,
        "reason": "no committed sample can carry the value (single-slot only)",
    }


# ------------------------------------------------- block/entry helpers


def _entry(start, end):
    return {"catalog_state_start": start, "catalog_state_end": end}


def _ok_block(value="v"):
    return {"status": "ok", "reason": None,
            "types": {"SeriesName": {"value": value, "resolved": True}}}


def test_entry_stable_canonical_truth_table():
    ok_a = catalog_canary.canonical_state(_ok_block("v"))
    assert catalog_canary.entry_stable_canonical(_entry(_ok_block("v"), _ok_block("v"))) == ok_a
    # changed mid-session: no ONE state describes the session
    assert catalog_canary.entry_stable_canonical(_entry(_ok_block("v"), _ok_block("w"))) is None
    # not_run on either side: unknown
    not_run = catalog_canary.not_run_block("reason")
    assert catalog_canary.entry_stable_canonical(_entry(not_run, _ok_block("v"))) is None
    assert catalog_canary.entry_stable_canonical(_entry(_ok_block("v"), None)) is None
    assert catalog_canary.entry_stable_canonical(None) is None


def test_entry_state_changed_truth_table():
    assert catalog_canary.entry_state_changed(_entry(_ok_block("v"), _ok_block("w"))) is True
    assert catalog_canary.entry_state_changed(_entry(_ok_block("v"), _ok_block("v"))) is False
    assert catalog_canary.entry_state_changed(_entry(catalog_canary.not_run_block("r"),
                                                      _ok_block("v"))) is None
    assert catalog_canary.entry_state_changed(_entry(_ok_block("v"), None)) is None


_UNKNOWN_BLOCK = {
    "status": "ok", "reason": None,
    "types": {"SeriesName": {"value": "v", "resolved": None,
                             "code": None, "authority": None,
                             "error": "SmapiError: cli hung"}},
}


def test_transient_probe_error_is_unknown_never_changed():
    """A probe error at one endcap must not read as a catalog CHANGE: the
    state degrades to unknown in both the stats and the triage signals."""
    assert catalog_canary.entry_state_changed(_entry(_ok_block("v"), _UNKNOWN_BLOCK)) is None
    assert catalog_canary.entry_state_changed(_entry(_UNKNOWN_BLOCK, _UNKNOWN_BLOCK)) is None
    assert catalog_canary.entry_stable_canonical(_entry(_UNKNOWN_BLOCK, _UNKNOWN_BLOCK)) is None


def test_vacuous_block_never_proves_a_pair():
    """Types declared but NONE probeable: the block proves nothing, so two
    identical vacuous sessions still have no comparable state."""
    vacuous = {"status": "ok", "reason": None,
               "types": {"MovieTitle": {"probeable": False,
                                        "reason": "no committed sample"}}}
    assert catalog_canary.entry_stable_canonical(_entry(vacuous, vacuous)) is None
    # but a locale that wires NO catalog types cannot be catalog-dead: its
    # empty block stays comparable
    empty = {"status": "ok", "reason": None, "types": {}}
    assert catalog_canary.entry_stable_canonical(_entry(empty, empty)) \
        == catalog_canary.canonical_state(empty)


def test_entry_type_failures_per_phase_and_value():
    start = {"status": "ok", "reason": None,
             "types": {"SeriesName": {"value": "coldplay", "resolved": False}}}
    end = {"status": "ok", "reason": None,
           "types": {"SeriesName": {"value": "coldplay", "resolved": True}}}
    failures = catalog_canary.entry_type_failures(_entry(start, end), "SeriesName")
    assert failures == [{"value": "coldplay", "phase": "start"}]
    # passed both sides: no failure
    assert catalog_canary.entry_type_failures(_entry(_ok_block(), _ok_block()), "SeriesName") == []
    # never probed (not_run): unknown, not failed
    assert catalog_canary.entry_type_failures(
        _entry(catalog_canary.not_run_block("r"), catalog_canary.not_run_block("r")),
        "SeriesName") == []
    # a probeable False entry (not-probeable typing) is not a failed probe
    absent = {"status": "ok", "reason": None,
              "types": {"MovieTitle": {"probeable": False, "reason": "no sample"}}}
    assert catalog_canary.entry_type_failures(_entry(absent, absent), "MovieTitle") == []


# ------------------------------------------------ environment plumbing


def test_env_block_disabled_by_nlu_canary_zero(monkeypatch):
    monkeypatch.setenv("NLU_CANARY", "0")
    monkeypatch.setenv("NLU_CANARY_SNAPSHOT", "/tmp/does-not-matter.json")
    calls = []
    block = catalog_canary.environment_block("it-IT", lambda u: calls.append(u))
    assert block["status"] == "not_run"
    assert block["reason"] == catalog_canary.REASON_DISABLED
    assert calls == []  # no probe was fired


def test_env_block_no_snapshot_reason(monkeypatch):
    monkeypatch.delenv("NLU_CANARY", raising=False)
    monkeypatch.delenv("NLU_CANARY_SNAPSHOT", raising=False)
    block = catalog_canary.environment_block("it-IT", lambda u: {})
    assert block["status"] == "not_run"
    assert block["reason"] == catalog_canary.REASON_NO_SNAPSHOT
    assert "NLU_CANARY_SNAPSHOT" in block["reason"]


def test_env_block_unreadable_snapshot_reason(monkeypatch, tmp_path):
    monkeypatch.delenv("NLU_CANARY", raising=False)
    bad = tmp_path / "bad.json"
    bad.write_text("{not json", encoding="utf-8")
    monkeypatch.setenv("NLU_CANARY_SNAPSHOT", str(bad))
    block = catalog_canary.environment_block("it-IT", lambda u: {})
    assert block["status"] == "not_run"
    assert block["reason"].startswith("catalog snapshot unreadable:")


def test_env_block_planning_failure_is_explicit_not_run(monkeypatch, tmp_path):
    monkeypatch.delenv("NLU_CANARY", raising=False)
    snapshot = tmp_path / "snap.json"
    snapshot.write_text(json.dumps(_snapshot({"SeriesName": "v"})), encoding="utf-8")
    monkeypatch.setenv("NLU_CANARY_SNAPSHOT", str(snapshot))

    def boom(*a, **k):
        raise FileNotFoundError("model dir gone")
    monkeypatch.setattr(catalog_canary, "build_canary_plan", boom)
    block = catalog_canary.environment_block("xx-XX", lambda u: {})
    assert block["status"] == "not_run"
    assert block["reason"].startswith("catalog canary failed:")
    assert "model dir gone" in block["reason"]


def test_env_block_happy_path_probes_and_returns_ok(monkeypatch, tmp_path):
    monkeypatch.delenv("NLU_CANARY", raising=False)
    snapshot = tmp_path / "snap.json"
    snapshot.write_text(json.dumps(_snapshot({"SeriesName": "Breaking Bad"})),
                        encoding="utf-8")
    monkeypatch.setenv("NLU_CANARY_SNAPSHOT", str(snapshot))
    plan = [{
        "type": "SeriesName", "value": "Breaking Bad",
        "intent": "PlayEpisodeIntent", "slot": "series_name",
        "utterance": "sigue con Breaking Bad",
    }]
    monkeypatch.setattr(catalog_canary, "build_canary_plan", lambda *a, **k: plan)
    seen = []
    block = catalog_canary.environment_block(
        "xx-XX",
        lambda u: seen.append(u) or _ok_response("PlayEpisodeIntent",
                                                 "series_name", "Breaking Bad"),
    )
    assert block["status"] == "ok"
    assert block["types"]["SeriesName"]["resolved"] is True
    assert seen == ["sigue con Breaking Bad"]


def test_canary_enabled_truth_table(monkeypatch):
    monkeypatch.delenv("NLU_CANARY", raising=False)
    assert catalog_canary.canary_enabled() is True
    monkeypatch.setenv("NLU_CANARY", "0")
    assert catalog_canary.canary_enabled() is False
    monkeypatch.setenv("NLU_CANARY", "1")
    assert catalog_canary.canary_enabled() is True


# ------------------------------------------------ ledger record emission


_UNSET = object()


def _ledger_writer(out_dir: Path, pid: int, minute: int, catalog_block=None,
                   reader_raises: bool = False, catalog_reader=_UNSET):
    """A writer with fixed everything; the catalog reader is explicit.

    Passing catalog_reader=None really wires NO reader (the explicit
    not-run path); leaving it unset defaults to a reader returning
    catalog_block (or a stable ok block when that is None too).
    """
    if catalog_reader is _UNSET:
        if reader_raises:
            def catalog_reader(_locale):
                raise RuntimeError("reader exploded")
        else:
            block = catalog_block if catalog_block is not None else _ok_block("Breaking Bad")
            catalog_reader = lambda _locale: block  # noqa: E731

    return nlu_ledger.LedgerWriter(
        out_dir=out_dir,
        clock=lambda: FIXED_MOMENT.replace(minute=minute),
        git_reader=lambda: ("head123", False),
        identity_reader=lambda locale: "etag-A",
        model_sha256_fn=lambda locale: "sha-A",
        pid=pid,
        catalog_reader=catalog_reader,
    )


def _record_session(out_dir: Path, pid: int, minute: int, actual_intent: str,
                    **writer_kwargs) -> None:
    writer = _ledger_writer(out_dir, pid, minute, **writer_kwargs)
    writer.start_session("sk-1")
    writer.record_row(locale="xx-XX", utterance="reproduce la cosa",
                      expected_intent="PlayEpisodeIntent",
                      actual_intent=actual_intent, outcome="pass")
    writer.finish()


def test_writer_records_catalog_start_and_end(tmp_path):
    block = _ok_block("Breaking Bad")
    _record_session(tmp_path, pid=301, minute=0, actual_intent="A", catalog_block=block)
    (path,) = tmp_path.glob("nlu-*.jsonl")
    records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
    locale_start = next(r for r in records if r["type"] == "locale_start")
    session_end = next(r for r in records if r["type"] == "session_end")
    assert locale_start["catalog_state_start"] == block
    assert session_end["catalog_state_end"] == {"xx-XX": block}
    sessions = nlu_ledger.load_sessions(tmp_path)
    entry = sessions[0]["locales"]["xx-XX"]
    assert entry["catalog_state_start"] == block
    assert entry["catalog_state_end"] == block


def test_writer_unwired_reader_records_explicit_not_run(tmp_path):
    writer = _ledger_writer(tmp_path, pid=302, minute=0, catalog_reader=None)
    writer.start_session("sk-1")
    writer.record_row(locale="xx-XX", utterance="reproduce la cosa",
                      expected_intent="X", actual_intent="X", outcome="pass")
    writer.finish()
    sessions = nlu_ledger.load_sessions(tmp_path)
    entry = sessions[0]["locales"]["xx-XX"]
    assert entry["catalog_state_start"]["status"] == "not_run"
    assert entry["catalog_state_start"]["reason"] == catalog_canary.REASON_UNWIRED_READER
    assert entry["catalog_state_end"]["reason"] == catalog_canary.REASON_UNWIRED_READER


def test_writer_reader_fault_records_not_run_and_session_continues(tmp_path):
    _record_session(tmp_path, pid=303, minute=0, actual_intent="A", reader_raises=True)
    sessions = nlu_ledger.load_sessions(tmp_path)
    entry = sessions[0]["locales"]["xx-XX"]
    assert entry["catalog_state_start"]["status"] == "not_run"
    assert entry["catalog_state_start"]["reason"] == catalog_canary.REASON_READER_FAILED
    assert sessions[0]["rows"]  # the session kept its row


# ------------------------------------------------ stats exclusion rules


def _flip_pair(out_dir: Path, blocks, pid_base=400):
    """Two same-sha, same-identity sessions with the given per-session
    catalog blocks; the row flips between them. An entry may be one block
    (same at both endcaps) or a (start, end) pair (a mid-session change)."""
    for index, block_or_pair in enumerate(blocks):
        pair = block_or_pair if isinstance(block_or_pair, tuple) \
            else (block_or_pair, block_or_pair)
        phases = iter(pair)

        def catalog_reader(_locale, _phases=phases):
            return next(_phases)

        writer = _ledger_writer(out_dir, pid_base + index, minute=index,
                                catalog_reader=catalog_reader)
        writer.start_session("sk-1")
        writer.record_row(locale="xx-XX", utterance="reproduce la cosa",
                          expected_intent="PlayEpisodeIntent",
                          actual_intent="A" if index == 0 else "B",
                          outcome="pass")
        writer.finish()


def _replace_session_blocks(path: Path, locale: str, start, end) -> None:
    """Rewrite one session file's catalog blocks (used to synthesize legacy
    sessions recorded before JF-971)."""
    lines = []
    for line in path.read_text(encoding="utf-8").splitlines():
        record = json.loads(line)
        if record.get("type") == "locale_start":
            record["catalog_state_start"] = start
        elif record.get("type") == "session_end":
            record["catalog_state_end"] = {locale: end}
        lines.append(json.dumps(record, sort_keys=True))
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def test_flip_with_equal_ok_states_counts_same_model(tmp_path):
    block = _ok_block("v")
    _flip_pair(tmp_path, [block, block])
    (row,) = nlu_ledger.compute_stats(nlu_ledger.load_sessions(tmp_path))["rows"]
    assert row["flips_total"] == 1
    assert row["flips_same_model"] == 1
    assert row["flips_catalog_excluded"] == 0


def test_flip_with_one_not_run_session_is_excluded_and_visible(tmp_path):
    _flip_pair(tmp_path, [_ok_block("v"),
                          catalog_canary.not_run_block("no snapshot")])
    (row,) = nlu_ledger.compute_stats(nlu_ledger.load_sessions(tmp_path))["rows"]
    assert row["flips_total"] == 1
    assert row["flips_same_model"] == 0  # AC 2: unknown state never proves a flip
    assert row["flips_catalog_excluded"] == 1  # the exclusion is visible, not silent


def test_flip_with_unknown_probe_session_is_excluded(tmp_path):
    """A transient probe error at one endcap leaves the state unknown: the
    pair is excluded from flips_same_model, never counted as proven."""
    _flip_pair(tmp_path, [_ok_block("v"), _UNKNOWN_BLOCK])
    (row,) = nlu_ledger.compute_stats(nlu_ledger.load_sessions(tmp_path))["rows"]
    assert row["flips_same_model"] == 0
    assert row["flips_catalog_excluded"] == 1


def test_flip_between_vacuous_sessions_is_excluded(tmp_path):
    """Identical all-not-probeable blocks prove nothing: excluded."""
    vacuous = {"status": "ok", "reason": None,
               "types": {"MovieTitle": {"probeable": False,
                                        "reason": "no committed sample"}}}
    _flip_pair(tmp_path, [vacuous, vacuous])
    (row,) = nlu_ledger.compute_stats(nlu_ledger.load_sessions(tmp_path))["rows"]
    assert row["flips_same_model"] == 0
    assert row["flips_catalog_excluded"] == 1


def test_flip_with_changed_state_is_excluded(tmp_path):
    changed = (_ok_block("v"), _ok_block("w"))  # the catalog changed mid-session
    _flip_pair(tmp_path, [changed, _ok_block("v")])
    (row,) = nlu_ledger.compute_stats(nlu_ledger.load_sessions(tmp_path))["rows"]
    assert row["flips_same_model"] == 0
    assert row["flips_catalog_excluded"] == 1


def test_flip_between_identical_failed_states_is_still_excluded(tmp_path):
    """Gate 2's asymmetry in the stats: a session whose canary FAILED is a
    catalog-state session even when the next session failed identically, so
    the pair is excluded rather than attributed to trainer noise."""
    failed = {"status": "ok", "reason": None,
              "types": {"SeriesName": {"value": "v", "resolved": False}}}
    _flip_pair(tmp_path, [failed, failed])
    (row,) = nlu_ledger.compute_stats(nlu_ledger.load_sessions(tmp_path))["rows"]
    assert row["flips_same_model"] == 0
    assert row["flips_catalog_excluded"] == 1


def test_flip_between_legacy_sessions_without_catalog_fields_is_excluded(tmp_path):
    """Sessions recorded before JF-971 carry no catalog fields; they read as
    UNKNOWN and their pairs leave flips_same_model (the honest direction)."""
    _flip_pair(tmp_path, [_ok_block("v"), _ok_block("v")])
    for path in sorted(tmp_path.glob("nlu-*.jsonl")):
        _replace_session_blocks(path, "xx-XX", None, None)
    (row,) = nlu_ledger.compute_stats(nlu_ledger.load_sessions(tmp_path))["rows"]
    assert row["flips_same_model"] == 0
    assert row["flips_catalog_excluded"] == 1


def test_flip_with_different_sha_is_not_catalog_excluded(tmp_path):
    """The counter is specifically the catalog-only exclusion: a pair whose
    sha differs was never same-model and must not double-count."""
    writer_a = _ledger_writer(tmp_path, pid=501, minute=0, catalog_block=_ok_block("v"))
    writer_a.start_session("sk-1")
    writer_a.record_row(locale="xx-XX", utterance="reproduce la cosa",
                        expected_intent="X", actual_intent="A", outcome="pass")
    writer_a.finish()
    writer_b = _ledger_writer(tmp_path, pid=502, minute=1, catalog_block=_ok_block("v"))
    writer_b._model_sha256_fn = lambda locale: "sha-B"
    writer_b.start_session("sk-1")
    writer_b.record_row(locale="xx-XX", utterance="reproduce la cosa",
                        expected_intent="X", actual_intent="B", outcome="pass")
    writer_b.finish()
    (row,) = nlu_ledger.compute_stats(nlu_ledger.load_sessions(tmp_path))["rows"]
    assert row["flips_same_model"] == 0
    assert row["flips_catalog_excluded"] == 0


def test_session_payload_and_text_show_the_catalog_summary(tmp_path):
    block = _ok_block("v")
    _flip_pair(tmp_path, [block, block])
    sessions = nlu_ledger.load_sessions(tmp_path)
    stats = nlu_ledger.compute_stats(sessions)
    for session in stats["sessions"]:
        catalog = session["locales"]["xx-XX"]["catalog"]
        assert catalog == {"start": "ok", "end": "ok", "stable": True}
    text = nlu_ledger.render_stats_text(stats)
    assert "catalog=ok/ok" in text
    assert "catalog_stable=true" in text
    assert "flips_catalog_excluded=0" in text


def test_session_payload_shows_not_run_summary(tmp_path):
    not_run = catalog_canary.not_run_block("no snapshot")
    _flip_pair(tmp_path, [not_run, not_run])
    stats = nlu_ledger.compute_stats(nlu_ledger.load_sessions(tmp_path))
    catalog = stats["sessions"][0]["locales"]["xx-XX"]["catalog"]
    assert catalog == {"start": "not_run", "end": "not_run", "stable": False}
    assert "catalog=not_run/not_run" in nlu_ledger.render_stats_text(stats)


# ------------------------------------------------ triage CATALOG_STATE


def _triage_model(out_dir: Path) -> Path:
    _write_model(out_dir)
    return out_dir


def _triage_session(out_dir: Path, pid: int, minute: int, rows, *,
                    catalog_blocks=None, identity="etag-A", identity_end=None):
    """One triage session; rows are (utterance, expected, actual, outcome, slots)."""
    start = catalog_blocks[0] if catalog_blocks else _ok_block("Breaking Bad")
    end = catalog_blocks[1] if catalog_blocks and len(catalog_blocks) > 1 else start
    identities = [identity, identity_end or identity]
    writer = _ledger_writer(out_dir, pid, minute)
    writer._identity_reader = lambda locale: identities.pop(0) if identities else identity
    writer._catalog_reader = lambda locale, pair=(start, end): pair[0]
    writer.start_session("sk-1")
    writer.start_locale("xx-XX")
    for utterance, expected, actual, outcome, slots in rows:
        writer.record_row(locale="xx-XX", utterance=utterance, expected_intent=expected,
                          expected_slots=slots, actual_intent=actual, slots={},
                          outcome=outcome, failure_kind=None)
    writer._catalog_reader = lambda locale, pair=(start, end): pair[1]
    writer.finish()


_FAILED_BLOCK = {
    "status": "ok", "reason": None,
    "types": {"SeriesName": {"value": "Breaking Bad", "resolved": False,
                             "code": None, "authority": None}},
}
_NOT_RUN_BLOCK = catalog_canary.not_run_block("no snapshot")

_RED_CATALOG_ROW = (
    "reproduce breaking bad temporada cinco", "PlayEpisodeIntent",
    "PlaySongIntent", nlu_ledger.OUTCOME_FAIL,
    {"series_name": {"value": "breaking bad"}},
)


def _triage(out_dir: Path, models_dir: Path, **kwargs):
    return nlu_triage.triage(nlu_ledger.load_sessions(out_dir),
                             models_dir=models_dir, **kwargs)


def _row(payload, utterance):
    matches = [r for r in payload["rows"] if r["utterance"] == utterance]
    assert len(matches) == 1, matches
    return matches[0]


def test_verdict_catalog_state_on_failed_canary(tmp_path):
    models = _triage_model(tmp_path / "models")
    led = tmp_path / "led"
    _triage_session(led, 601, 0, [_RED_CATALOG_ROW], catalog_blocks=[_FAILED_BLOCK])
    payload = _triage(led, models)
    row = _row(payload, _RED_CATALOG_ROW[0])
    assert row["verdict"] == nlu_triage.V_CATALOG_STATE
    assert any("unresolved" in d and "Breaking Bad" in d for d in row["detail"])
    assert row["catalog_types"] == ["SeriesName"]
    assert row["catalog_failed"][0]["phase"] == "start"


def test_verdict_catalog_state_on_changed_state(tmp_path):
    models = _triage_model(tmp_path / "models")
    led = tmp_path / "led"
    _triage_session(led, 602, 0, [_RED_CATALOG_ROW],
                    catalog_blocks=[_ok_block("v1"), _ok_block("v2")])
    payload = _triage(led, models)
    row = _row(payload, _RED_CATALOG_ROW[0])
    assert row["verdict"] == nlu_triage.V_CATALOG_STATE
    assert any("state changed between session start and end" in d for d in row["detail"])


def test_passed_canary_does_not_fire_catalog_state(tmp_path):
    models = _triage_model(tmp_path / "models")
    led = tmp_path / "led"
    _triage_session(led, 603, 0, [_RED_CATALOG_ROW])
    payload = _triage(led, models)
    row = _row(payload, _RED_CATALOG_ROW[0])
    assert row["verdict"] != nlu_triage.V_CATALOG_STATE
    assert row["catalog_failed"] == []
    assert row["catalog_changed"] is False


def test_not_run_canary_never_fires_catalog_state(tmp_path):
    models = _triage_model(tmp_path / "models")
    led = tmp_path / "led"
    _triage_session(led, 604, 0, [_RED_CATALOG_ROW],
                    catalog_blocks=[_NOT_RUN_BLOCK, _NOT_RUN_BLOCK])
    payload = _triage(led, models)
    row = _row(payload, _RED_CATALOG_ROW[0])
    assert row["verdict"] != nlu_triage.V_CATALOG_STATE


def test_failed_canary_without_catalog_slotted_intent_does_not_fire(tmp_path):
    """The verdict is for CATALOG-slotted reds: a red row whose expected
    intents declare no catalog type stays lower down the precedence."""
    models = _triage_model(tmp_path / "models")
    led = tmp_path / "led"
    static_row = ("dime la cosa", "StaticIntent", "StaticIntent",
                  nlu_ledger.OUTCOME_FAIL, {"thing": {"value": "la cosa"}})
    _triage_session(led, 605, 0, [static_row], catalog_blocks=[_FAILED_BLOCK])
    payload = _triage(led, models)
    row = _row(payload, "dime la cosa")
    assert row["verdict"] != nlu_triage.V_CATALOG_STATE
    assert row["catalog_types"] == []


def test_precedence_infra_beats_catalog_state(tmp_path):
    models = _triage_model(tmp_path / "models")
    led = tmp_path / "led"
    error_row = ("reproduce breaking bad temporada cinco", "PlayEpisodeIntent",
                 None, nlu_ledger.OUTCOME_ERROR, {}, )
    _triage_session(led, 606, 0, [error_row], catalog_blocks=[_FAILED_BLOCK])
    payload = _triage(led, models)
    assert _row(payload, error_row[0])["verdict"] == nlu_triage.V_INFRA


def test_precedence_unstable_build_beats_catalog_state(tmp_path):
    models = _triage_model(tmp_path / "models")
    led = tmp_path / "led"
    _triage_session(led, 607, 0, [_RED_CATALOG_ROW], catalog_blocks=[_FAILED_BLOCK],
                    identity="etag-before", identity_end="etag-after")
    payload = _triage(led, models)
    assert _row(payload, _RED_CATALOG_ROW[0])["verdict"] == nlu_triage.V_UNSTABLE_BUILD


def test_precedence_catalog_state_beats_fixture_vocab(tmp_path):
    """AC 3: the CATALOG_STATE verdict is placed before FIXTURE_VOCAB. A red
    row whose pinned value is absent from the seed AND whose canary failed
    reads CATALOG_STATE: the catalog state disqualifies the session first."""
    models = _triage_model(tmp_path / "models")
    led = tmp_path / "led"
    # a value the committed seed does not carry: the vocab rule would fire
    absent_row = ("sigue con casa de papel", "PlayEpisodeIntent", "PlaySongIntent",
                  nlu_ledger.OUTCOME_FAIL, {"series_name": {"value": "casa de papel"}})
    _triage_session(led, 608, 0, [absent_row], catalog_blocks=[_FAILED_BLOCK])
    payload = _triage(led, models)
    row = _row(payload, absent_row[0])
    assert row["verdict"] == nlu_triage.V_CATALOG_STATE
    assert row["vocab_status"] is not None  # the lower rule had a chance and lost


def test_verdict_order_pins_catalog_state_placement():
    order = nlu_triage.VERDICT_ORDER
    assert order.index(nlu_triage.V_CATALOG_STATE) == order.index(nlu_triage.V_UNSTABLE_BUILD) + 1
    assert order.index(nlu_triage.V_CATALOG_STATE) < order.index(nlu_triage.V_FIXTURE_VOCAB)


def test_triage_doc_carries_the_catalog_state_contract():
    docs = (REPO / "docs" / "nlu-static-analysis.md").read_text(encoding="utf-8")
    assert "CATALOG_STATE" in docs
    assert "NLU_CANARY" in docs
    assert "NLU_CANARY_SNAPSHOT" in docs

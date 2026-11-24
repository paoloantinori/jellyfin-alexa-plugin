"""JF-954.2: the token-ownership collision lint battery.

Synthetic models are written into tmp_path as bare languageModel dicts; no
test touches a committed model file, the network, SMAPI or Jellyfin. The
CLI tests redirect the models directory and the baseline file to tmp_path
through the module's two test seams (default_models_dir /
default_baseline_path) so the committed baseline and models stay untouched.
"""

import json
import sys
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from nlu_static import cli as nlu_cli  # noqa: E402
from nlu_static import token_collisions as tc  # noqa: E402
from validate_interaction_models import lint_token_collisions  # noqa: E402


def _write_model(models_dir: Path, locale: str, model: dict) -> Path:
    """Write one synthetic bare languageModel document for a locale."""
    path = models_dir / f"model_{locale}.json"
    path.write_text(json.dumps(model, ensure_ascii=False), encoding="utf-8")
    return path


def _intent(name: str, samples: list[str], slots: list[dict] | None = None) -> dict:
    intent = {"name": name, "samples": samples}
    if slots:
        intent["slots"] = slots
    return intent


def _type(name: str, entries: list[dict]) -> dict:
    return {"name": name, "values": [{"name": entry} for entry in entries]}


def _entry(value: str, synonyms: list[str] | None = None) -> dict:
    return {"value": value, "synonyms": synonyms or []}


def _issue46_model() -> dict:
    """The miniature issue #46 model the required regression test runs on.

    MediaInfoType owns value "info" with synonyms "Lied" and "Song";
    MediaInfoIntent uses it; PlaySongIntent carries "Lied" as a literal
    carrier word and {song} only as a slot name.
    """
    return {
        "invocationName": "test",
        "intents": [
            _intent(
                "MediaInfoIntent",
                ["Was ist das fur ein {media_type}", "Wie heisst der Song"],
                slots=[{"name": "media_type", "type": "MediaInfoType"}],
            ),
            _intent(
                "PlaySongIntent",
                ["Spiele das Lied {song}", "Spiele {song}", "Spiele {song} von {musician}"],
                slots=[
                    {"name": "song", "type": "AMAZON.MusicRecording"},
                    {"name": "musician", "type": "AMAZON.Musician"},
                ],
            ),
        ],
        "types": [
            _type("MediaInfoType", [_entry("info", ["Lied", "Song"])]),
        ],
    }


def _findings_for(
    model: dict, locale: str, tmp_path: Path, snapshot: dict | None = None
) -> list:
    """Analyze a synthetic model from the test's tmp_path; no commit touch."""
    _write_model(tmp_path, locale, model)
    report = tc.analyze_locale(locale, snapshot=snapshot, models_dir=tmp_path)
    return list(report.findings) + list(report.catalog_findings)


def _snapshot(table: dict) -> dict:
    return {"schema": 1, "producer": "test", "locales": {"*": table}}


# --- the required regression test (issue #46 shape) -------------------------------------


def test_issue46_mini_model_yields_exactly_one_lied_finding(tmp_path):
    """AC 1: exactly one CARRIER_VS_VALUE, for entry lied against PlaySongIntent."""
    findings = _findings_for(_issue46_model(), "de-DE", tmp_path)
    carrier = [f for f in findings if f.kind == tc.KIND_CARRIER_VS_VALUE]
    assert len(carrier) == 1
    finding = carrier[0]
    assert finding.locale == "de-DE"
    assert finding.type_names == ("MediaInfoType",)
    assert finding.entry == "lied"
    assert finding.intent == "PlaySongIntent"
    assert finding.detail.startswith("using intents: MediaInfoIntent; samples: 1;")
    assert finding.identity() == "de-DE|CARRIER_VS_VALUE|MediaInfoType|lied|PlaySongIntent"


def test_issue46_slot_name_song_produces_no_finding(tmp_path):
    """The {song} placeholder is removed before tokenizing, so no song finding."""
    findings = _findings_for(_issue46_model(), "de-DE", tmp_path)
    assert not any(f.entry == "song" for f in findings)


def test_issue46_example_sample_is_the_alphabetically_first_hit(tmp_path):
    findings = _findings_for(_issue46_model(), "de-DE", tmp_path)
    finding = [f for f in findings if f.kind == tc.KIND_CARRIER_VS_VALUE][0]
    assert '"Spiele das Lied {song}"' in finding.detail


def test_multi_token_entry_matches_contiguously_only(tmp_path):
    """A multi-token entry matches the contiguous run, not a scattered one."""
    model = {
        "invocationName": "test",
        "intents": [
            _intent(
                "InfoIntent",
                ["Wie heisst die {media_type}"],
                slots=[{"name": "media_type", "type": "InfoType"}],
            ),
            _intent(
                "PlayerIntent",
                ["Spiele das lied die toentiere", "Spiele lied toentiere {song}"],
            ),
        ],
        "types": [_type("InfoType", [_entry("das lied die")])],
    }
    findings = _findings_for(model, "de-DE", tmp_path)
    carrier = [f for f in findings if f.kind == tc.KIND_CARRIER_VS_VALUE]
    # "das lied die" matches sample 1 contiguously; sample 2 holds only the
    # scattered pair, which must not add a second finding or a false count.
    assert len(carrier) == 1
    assert "samples: 1" in carrier[0].detail
    assert '"Spiele das lied die toentiere"' in carrier[0].detail


# --- negative cases ---------------------------------------------------------------------


def test_entry_only_in_carriers_of_owning_intent_yields_no_finding(tmp_path):
    """AC 2: an entry found only in carriers of intents that use its type is silent."""
    model = {
        "invocationName": "test",
        "intents": [
            _intent(
                "InfoIntent",
                ["Wie heisst das lied"],
                slots=[{"name": "media_type", "type": "InfoType"}],
            ),
            _intent("InfoIntentZwei", ["Nenne das lied"], slots=[{"name": "t", "type": "InfoType"}]),
        ],
        "types": [_type("InfoType", [_entry("lied")])],
    }
    findings = _findings_for(model, "de-DE", tmp_path)
    assert not [f for f in findings if f.kind == tc.KIND_CARRIER_VS_VALUE]


def test_type_used_by_no_intent_yields_no_carrier_finding(tmp_path):
    """Without a using intent J there is no collision by definition."""
    model = {
        "invocationName": "test",
        "intents": [_intent("PlayerIntent", ["Spiele das lied {song}"])],
        "types": [_type("InfoType", [_entry("lied")])],
    }
    findings = _findings_for(model, "de-DE", tmp_path)
    assert not [f for f in findings if f.kind == tc.KIND_CARRIER_VS_VALUE]


def test_intent_using_the_type_is_never_a_match_target(tmp_path):
    """An intent declaring the type never counts as its own collision target."""
    model = {
        "invocationName": "test",
        "intents": [
            _intent(
                "InfoIntent",
                ["Wie heisst das lied", "Nenne das lied"],
                slots=[{"name": "media_type", "type": "InfoType"}],
            ),
            _intent(
                "OtherIntent",
                ["Was ist das lied hier"],
                slots=[{"name": "x", "type": "InfoType"}],
            ),
        ],
        "types": [_type("InfoType", [_entry("lied")])],
    }
    findings = _findings_for(model, "de-DE", tmp_path)
    assert not [f for f in findings if f.kind == tc.KIND_CARRIER_VS_VALUE]


def test_empty_after_tokenization_entries_are_skipped(tmp_path):
    """An entry that tokenizes to nothing produces neither findings nor crashes."""
    model = {
        "invocationName": "test",
        "intents": [
            _intent(
                "InfoIntent",
                ["Wie heisst das lied"],
                slots=[{"name": "media_type", "type": "InfoType"}],
            ),
            _intent("PlayerIntent", ["Spiele das lied {song}"]),
        ],
        "types": [_type("InfoType", [_entry("!"), _entry("lied")])],
    }
    findings = _findings_for(model, "de-DE", tmp_path)
    carrier = [f for f in findings if f.kind == tc.KIND_CARRIER_VS_VALUE]
    assert [f.entry for f in carrier] == ["lied"]


# --- VALUE_IN_TWO_TYPES -----------------------------------------------------------------


def test_value_in_two_types_one_finding_per_sorted_pair(tmp_path):
    """AC 3: one finding per entry and sorted type pair; three types give three pairs."""
    model = {
        "invocationName": "test",
        "intents": [_intent("PlayerIntent", ["Spiele {song}"])],
        "types": [
            _type("ZetaType", [_entry("jazz")]),
            _type("AlphaType", [_entry("jazz")]),
            _type("MidType", [_entry("Jazz")]),
            _type("OtherType", [_entry("blues")]),
        ],
    }
    findings = [f for f in _findings_for(model, "en-US", tmp_path) if f.kind == tc.KIND_VALUE_IN_TWO_TYPES]
    pairs = sorted(f.type_names for f in findings)
    assert pairs == [
        ("AlphaType", "MidType"),
        ("AlphaType", "ZetaType"),
        ("MidType", "ZetaType"),
    ]
    assert all(f.entry == "jazz" and f.intent == "" for f in findings)
    # Casefold unifies "Jazz" and "jazz"; "blues" is owned once and is silent.
    assert not any(f.entry == "blues" for f in findings)


def test_spelling_variants_inside_one_type_do_not_pair_with_itself(tmp_path):
    model = {
        "invocationName": "test",
        "intents": [_intent("PlayerIntent", ["Spiele {song}"])],
        "types": [_type("SoloType", [_entry("jazz"), _entry("JAZZ")])],
    }
    findings = [f for f in _findings_for(model, "en-US", tmp_path) if f.kind == tc.KIND_VALUE_IN_TWO_TYPES]
    assert findings == []


# --- CATALOG_VS_MODEL -------------------------------------------------------------------


def test_catalog_vs_model_slot_free_sample_and_static_entry(tmp_path):
    """AC 4: both sub-kinds are reported with exact counts and never baselined."""
    model = {
        "invocationName": "test",
        "intents": [
            _intent("StopIntent", ["stop"]),
            _intent("PlayerIntent", ["Spiele {song}"]),
        ],
        "types": [
            # STATIC custom type owning "jazz"; SongTitle is catalog-backed.
            _type("GenreWordType", [_entry("jazz")]),
            _type("SongTitle", [_entry("seed-title")]),
        ],
    }
    snapshot = _snapshot({"SongTitle": [_entry("Stop"), _entry("Jazz"), _entry("Normal Song")]})
    _write_model(tmp_path, "en-US", model)
    report = tc.analyze_locale("en-US", snapshot=snapshot, models_dir=tmp_path)
    assert report.catalog_counts == (
        (tc.SUBKIND_SLOT_FREE_SAMPLE, 1),
        (tc.SUBKIND_STATIC_ENTRY, 1),
    )
    identities = [f.identity() for f in report.catalog_findings]
    assert identities == [
        "en-US|CATALOG_VS_MODEL|SongTitle|jazz|",
        "en-US|CATALOG_VS_MODEL|SongTitle|stop|",
    ]
    assert "sub-kind: slot_free_sample" in report.catalog_findings[1].detail
    assert "sub-kind: static_entry" in report.catalog_findings[0].detail
    # The catalog kind never joins the baseline candidate set; the pair
    # finding above ("jazz" owned by the STATIC type and the snapshot) does.
    assert tc.baseline_candidates([report]) == [
        "en-US|VALUE_IN_TWO_TYPES|GenreWordType,SongTitle|jazz|"
    ]


def test_catalog_vs_model_inactive_without_snapshot(tmp_path):
    model = {
        "invocationName": "test",
        "intents": [_intent("StopIntent", ["stop"])],
        "types": [_type("SongTitle", [_entry("stop")])],
    }
    _write_model(tmp_path, "en-US", model)
    report = tc.analyze_locale("en-US", models_dir=tmp_path)
    assert report.catalog_findings == ()
    assert report.catalog_counts == ()


def test_catalog_vs_model_capped_at_fifty_with_exact_counts(tmp_path):
    """The display list stops at 50 sorted findings; counts stay exact."""
    entries = [_entry(f"title {index:03d}") for index in range(60)]
    model = {
        "invocationName": "test",
        "intents": [_intent(f"Intent{index:03d}", [f"title {index:03d}"]) for index in range(60)],
        "types": [_type("SongTitle", [_entry("seed")])],
    }
    snapshot = _snapshot({"SongTitle": entries})
    _write_model(tmp_path, "en-US", model)
    report = tc.analyze_locale("en-US", snapshot=snapshot, models_dir=tmp_path)
    assert report.catalog_counts == (
        (tc.SUBKIND_SLOT_FREE_SAMPLE, 60),
        (tc.SUBKIND_STATIC_ENTRY, 0),
    )
    assert len(report.catalog_findings) == 50
    assert report.catalog_truncated == 10
    assert report.catalog_findings[0].entry == "title 000"
    assert report.catalog_findings[-1].entry == "title 049"


# --- baseline filtering -----------------------------------------------------------------


def test_baselined_findings_do_not_warn_and_a_new_one_does(tmp_path):
    """AC 5: split_baselined is the one filter point for validator and CLI."""
    findings = _findings_for(_issue46_model(), "de-DE", tmp_path)
    assert len(findings) == 1
    non_baselined, baselined = tc.split_baselined(findings, [findings[0].identity()])
    assert non_baselined == []
    assert len(baselined) == 1
    non_baselined2, baselined2 = tc.split_baselined(findings, [])
    assert len(non_baselined2) == 1
    assert baselined2 == []


def test_baseline_candidates_cover_both_model_kinds_only():
    reports = [
        tc.LocaleCollisions(
            locale="en-US",
            skip_reason=None,
            findings=(
                tc.CollisionFinding("en-US", tc.KIND_CARRIER_VS_VALUE, ("T",), "x", "I", "d"),
                tc.CollisionFinding("en-US", tc.KIND_VALUE_IN_TWO_TYPES, ("A", "B"), "y", "", "d"),
            ),
            catalog_findings=(
                tc.CollisionFinding("en-US", tc.KIND_CATALOG_VS_MODEL, ("C",), "z", "", "d"),
            ),
            catalog_counts=(),
            catalog_truncated=0,
        )
    ]
    assert tc.baseline_candidates(reports) == [
        "en-US|CARRIER_VS_VALUE|T|x|I",
        "en-US|VALUE_IN_TWO_TYPES|A,B|y|",
    ]


# --- baseline file and CLI flows --------------------------------------------------------


@pytest.fixture()
def isolated_tool(tmp_path, monkeypatch):
    """Point the lint at synthetic models and a tmp baseline file."""
    models_dir = tmp_path / "models"
    models_dir.mkdir()
    baseline_path = tmp_path / "baseline.json"
    monkeypatch.setattr(tc, "default_models_dir", lambda: models_dir)
    monkeypatch.setattr(tc, "default_baseline_path", lambda: baseline_path)
    return models_dir, baseline_path


def test_cli_update_baseline_twice_byte_identical_and_reports_delta(isolated_tool):
    """AC 6: the rewrite is deterministic; the first run adds, the second no-ops."""
    models_dir, baseline_path = isolated_tool
    _write_model(models_dir, "de-DE", _issue46_model())
    argv = ["tokens", "--update-baseline"]
    assert nlu_cli.main(argv) == 0
    content_first = baseline_path.read_text(encoding="utf-8")
    assert nlu_cli.main(argv) == 0
    content_second = baseline_path.read_text(encoding="utf-8")
    assert content_first == content_second
    assert json.loads(content_first) == ["de-DE|CARRIER_VS_VALUE|MediaInfoType|lied|PlaySongIntent"]


def test_cli_default_hides_baselined_and_all_shows_the_flag(isolated_tool, capsys):
    models_dir, _ = isolated_tool
    _write_model(models_dir, "de-DE", _issue46_model())
    assert nlu_cli.main(["tokens", "--update-baseline"]) == 0
    capsys.readouterr()
    assert nlu_cli.main(["tokens", "--locale", "de-DE"]) == 0
    default_out = capsys.readouterr().out
    assert "non-baselined findings: 0" in default_out
    assert "lied" not in default_out
    assert nlu_cli.main(["tokens", "--locale", "de-DE", "--all"]) == 0
    all_out = capsys.readouterr().out
    assert "de-DE|CARRIER_VS_VALUE|MediaInfoType|lied|PlaySongIntent [baselined: true]" in all_out


def test_cli_lists_stale_identities_only_for_linted_locales(isolated_tool, capsys):
    models_dir, baseline_path = isolated_tool
    stale_pair = [
        "de-DE|CARRIER_VS_VALUE|GoneType|old|DeletedIntent",
        "en-US|CARRIER_VS_VALUE|GoneType|old|DeletedIntent",
    ]
    baseline_path.write_text(json.dumps(stale_pair, indent=2) + "\n", encoding="utf-8")
    _write_model(models_dir, "de-DE", _issue46_model())
    _write_model(models_dir, "en-US", {"invocationName": "test", "intents": [], "types": []})
    assert nlu_cli.main(["tokens", "--locale", "de-DE"]) == 0
    single_out = capsys.readouterr().out
    assert "stale baseline identities: 1" in single_out
    assert "de-DE|CARRIER_VS_VALUE|GoneType|old|DeletedIntent" in single_out
    assert "en-US|" not in single_out
    assert nlu_cli.main(["tokens"]) == 0
    full_out = capsys.readouterr().out
    assert "stale baseline identities: 2" in full_out
    assert "en-US|CARRIER_VS_VALUE|GoneType|old|DeletedIntent" in full_out


def test_scoped_update_keeps_other_locales_entries(isolated_tool, capsys):
    """A scoped --update-baseline merges: other locales' entries survive."""
    models_dir, baseline_path = isolated_tool
    baseline_path.write_text(
        json.dumps(
            [
                "de-DE|CARRIER_VS_VALUE|GoneType|old|DeletedIntent",
                "en-US|CARRIER_VS_VALUE|GoneType|old|DeletedIntent",
            ],
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    _write_model(models_dir, "de-DE", _issue46_model())
    assert nlu_cli.main(["tokens", "--locale", "de-DE", "--update-baseline"]) == 0
    out = capsys.readouterr().out
    assert "added 1, removed 1" in out
    stored = json.loads(baseline_path.read_text(encoding="utf-8"))
    assert stored == [
        "de-DE|CARRIER_VS_VALUE|MediaInfoType|lied|PlaySongIntent",
        "en-US|CARRIER_VS_VALUE|GoneType|old|DeletedIntent",
    ]


def test_snapshot_with_update_baseline_refused(isolated_tool, tmp_path, capsys):
    """The baseline tracks the committed models; a snapshot run cannot write it."""
    models_dir, baseline_path = isolated_tool
    model = {
        "invocationName": "test",
        "intents": [_intent("StopIntent", ["stop"])],
        "types": [_type("SongTitle", [_entry("seed")])],
    }
    _write_model(models_dir, "en-US", model)
    snapshot_path = tmp_path / "snapshot.json"
    snapshot_path.write_text(
        json.dumps(_snapshot({"SongTitle": [_entry("stop")]})), encoding="utf-8"
    )
    assert nlu_cli.main(
        [
            "tokens",
            "--locale",
            "en-US",
            "--snapshot",
            str(snapshot_path),
            "--update-baseline",
        ]
    ) == 2
    assert not baseline_path.exists()
    assert "--snapshot" in capsys.readouterr().err


def test_cli_two_runs_byte_identical(isolated_tool, capsys):
    """Parent determinism contract: the same state prints identical bytes."""
    models_dir, _ = isolated_tool
    _write_model(models_dir, "de-DE", _issue46_model())
    capsys.readouterr()
    assert nlu_cli.main(["tokens", "--locale", "de-DE", "--all"]) == 0
    first = capsys.readouterr().out
    assert nlu_cli.main(["tokens", "--locale", "de-DE", "--all"]) == 0
    second = capsys.readouterr().out
    assert first == second
    assert "lied|PlaySongIntent" in first


def test_cli_snapshot_enables_catalog_kind_and_baseline_skips_it(isolated_tool, tmp_path, capsys):
    models_dir, baseline_path = isolated_tool
    model = {
        "invocationName": "test",
        "intents": [_intent("StopIntent", ["stop"])],
        "types": [_type("SongTitle", [_entry("seed")])],
    }
    _write_model(models_dir, "en-US", model)
    snapshot_path = tmp_path / "snapshot.json"
    snapshot_path.write_text(
        json.dumps(_snapshot({"SongTitle": [_entry("stop")]})), encoding="utf-8"
    )
    argv = ["tokens", "--locale", "en-US", "--snapshot", str(snapshot_path)]
    assert nlu_cli.main(argv) == 0
    out = capsys.readouterr().out
    assert "CATALOG_VS_MODEL en-US: slot_free_sample=1 static_entry=0" in out
    assert "en-US|CATALOG_VS_MODEL|SongTitle|stop|" in out
    # The snapshot run cannot write the baseline (committed models only);
    # see test_snapshot_with_update_baseline_refused for the refusal itself.


# --- determinism and skips --------------------------------------------------------------


def test_findings_sorted_by_identity_and_runs_are_stable(tmp_path):
    """The identity order holds across kinds and two runs agree exactly."""
    model = {
        "invocationName": "test",
        "intents": [
            _intent(
                "InfoIntent",
                ["Was ist das lied", "Wie heisst das lied"],
                slots=[{"name": "media_type", "type": "InfoType"}],
            ),
            _intent("PlayerIntent", ["Spiele das lied {song}", "Wie heisst das lied"]),
            _intent("ThirdIntent", ["Wie heisst das lied"]),
        ],
        "types": [
            _type("InfoType", [_entry("lied")]),
            _type("OtherType", [_entry("lied")]),
        ],
    }
    first = _findings_for(model, "de-DE", tmp_path)
    second = _findings_for(model, "de-DE", tmp_path)
    assert first == second
    identities = [f.identity() for f in first]
    assert identities == sorted(identities)
    assert len(identities) == len(set(identities))


def test_ja_jp_reports_skip_line_and_no_findings(tmp_path):
    """The SKIP contract: the shared line, nothing else, no crash."""
    _write_model(tmp_path, "ja-JP", _issue46_model())
    report = tc.analyze_locale("ja-JP", models_dir=tmp_path)
    assert report.skip_reason == "SKIP ja-JP: no whitespace tokenization"
    assert report.findings == () and report.catalog_findings == ()


def test_skip_line_shared_producer_matches_the_lint():
    assert tc.skip_line("ja-JP") == "SKIP ja-JP: no whitespace tokenization"
    assert tc.skip_line("de-DE") is None


def test_double_occurrence_in_one_sample_counts_once(tmp_path):
    """An entry twice in ONE sample is one matching sample in the detail."""
    model = {
        "invocationName": "test",
        "intents": [
            _intent(
                "InfoIntent",
                ["Was ist das lied"],
                slots=[{"name": "media_type", "type": "InfoType"}],
            ),
            _intent("PlayerIntent", ["Wie heisst das lied und lied zwei"]),
        ],
        "types": [_type("InfoType", [_entry("lied")])],
    }
    findings = [f for f in _findings_for(model, "de-DE", tmp_path) if f.kind == tc.KIND_CARRIER_VS_VALUE]
    assert len(findings) == 1
    assert "samples: 1" in findings[0].detail


def test_validator_lint_skips_loudly_on_unreadable_baseline(tmp_path, monkeypatch, capsys):
    """A malformed baseline file skips the phase with a SKIP line, never crashes."""
    bad = tmp_path / "baseline.json"
    bad.write_text('{"not": "a list"}', encoding="utf-8")
    monkeypatch.setattr(tc, "default_baseline_path", lambda: bad)
    model = _issue46_model()
    result = lint_token_collisions({"de-DE": model})
    assert result is None
    assert "SKIP Phase 11: baseline file unreadable" in capsys.readouterr().out


def test_raw_model_seam_matches_the_file_built_model(tmp_path):
    """The pre-parsed-model entry point returns the same model as the file path.

    This is the seam the validator's Phase 11 threads its parsed models
    through, so the files are read once per run and no second parser
    touches disk.
    """
    from nlu_static.effective_model import build_effective_model

    _write_model(tmp_path, "de-DE", _issue46_model())
    from_file = build_effective_model("de-DE", models_dir=tmp_path)
    raw = json.loads((tmp_path / "model_de-DE.json").read_text(encoding="utf-8"))
    from_raw = build_effective_model("de-DE", raw_model=raw)
    assert from_file == from_raw
    report_file = tc.analyze_locale("de-DE", models_dir=tmp_path)
    report_raw = tc.analyze_locale("de-DE", raw_model=raw)
    assert report_file == report_raw


def test_validator_lint_reports_unparsable_model_as_warning_not_crash(tmp_path, monkeypatch):
    """A model Phase 1 accepts but the strict parser rejects is a warning line.

    The phase is WARNING level: it must never raise out of run_warning_phase
    and flip the validator's exit code.
    """
    monkeypatch.setattr(tc, "default_baseline_path", lambda: tmp_path / "missing.json")
    malformed = {
        "invocationName": "test",
        "intents": [_intent("PlayerIntent", ["Spiele {song}"])],
        "types": [{"name": "BrokenType", "values": [{"name": {"nope": 1}}]}],
    }
    warnings = lint_token_collisions({"en-US": malformed})
    assert len(warnings) == 1
    assert "could not process the model" in warnings[0]
    assert "BrokenType" in warnings[0]

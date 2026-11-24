"""JF-954.3: the fixture-versus-vocabulary check battery (the Breaking Bad class).

Synthetic models and fixture row dicts are built inside each test; the only
committed-tree read is the AC 9 real-data test, which loads the committed
es-ES MODEL (allowed by the parent task note) and builds its fixture rows
INLINE, never reading the committed fixture file. No test touches the
network, SMAPI, the ask CLI or Jellyfin. The CLI tests redirect the models
and fixtures directories to tmp_path through the module's two test seams.
"""

import json
import sys
from pathlib import Path

import pytest
import yaml

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from nlu_static import cli as nlu_cli  # noqa: E402
from nlu_static import fixture_vocab as fv  # noqa: E402
from nlu_static.effective_model import build_effective_model  # noqa: E402
from validate_interaction_models import lint_fixture_vocab  # noqa: E402


def _model(intents: list[dict], types: list[dict]) -> dict:
    return {"invocationName": "test", "intents": intents, "types": types}


def _intent(name: str, samples: list[str], slots: list[dict] | None = None) -> dict:
    intent = {"name": name, "samples": samples}
    if slots:
        intent["slots"] = slots
    return intent


def _type(name: str, entries: list[dict]) -> dict:
    return {"name": name, "values": [{"name": entry} for entry in entries]}


def _entry(value: str, synonyms: list[str] | None = None) -> dict:
    return {"value": value, "synonyms": synonyms or []}


SERIES_TYPE = _type("SeriesName", [_entry("Breaking Bad"), _entry("La Casa de Papel")])
MOOD_TYPE = _type("Mood", [_entry("relaxing", ["calm"]), _entry("happy")])

_EPISODE_MODEL = _model(
    [
        _intent(
            "PlayEpisodeIntent",
            ["reproduce {series_name} temporada {season_number} episodio {episode_number}"],
            slots=[
                {"name": "series_name", "type": "SeriesName"},
                {"name": "season_number", "type": "AMAZON.NUMBER"},
                {"name": "episode_number", "type": "AMAZON.NUMBER"},
            ],
        ),
        _intent(
            "PlaySongIntent",
            ["pon {song}"],
            slots=[{"name": "song", "type": "AMAZON.MusicRecording"}],
        ),
        _intent(
            "PlayMoodMusicIntent",
            ["musica {mood}"],
            slots=[{"name": "mood", "type": "Mood"}],
        ),
    ],
    [SERIES_TYPE, MOOD_TYPE],
)


def _rows(*rows: dict) -> list[dict]:
    return list(rows)


def _episode_row(
    series_pin: object = {}, intent: str = "PlayEpisodeIntent", utterance: str | None = None
) -> dict:
    return {
        "utterance": utterance or "reproduce breaking bad temporada uno episodio tres",
        "expected_intent": intent,
        "expected_slots": {
            "series_name": series_pin,
            "season_number": {},
            "episode_number": {},
        },
    }


def _results(model: dict, rows: list[dict], **kwargs) -> list:
    return fv.check_fixture_rows(build_effective_model("es-ES", raw_model=model), rows, **kwargs)


def _snapshot(table: dict, producer: str = "jellyfin-names", locale: str = "*") -> dict:
    return {"schema": 1, "producer": producer, "locales": {locale: dict(table)}}


def _by_slot(results: list, slot: str) -> list:
    return [result for result in results if result.slot == slot]


# --- AC 1: pinned values, PRESENT/ABSENT, normalization insensitivity ------


def test_pinned_value_present_by_canonical():
    results = _results(_EPISODE_MODEL, _rows(_episode_row({"value": "Breaking Bad"})))
    assert _by_slot(results, "series_name")[0].status == fv.STATUS_PRESENT


def test_pinned_value_present_by_synonym():
    mood_rows = [
        {
            "utterance": "musica calm",
            "expected_intent": "PlayMoodMusicIntent",
            "expected_slots": {"mood": {"value": "calm"}},
        }
    ]
    results = _results(_EPISODE_MODEL, mood_rows)
    assert _by_slot(results, "mood")[0].status == fv.STATUS_PRESENT


def test_pinned_value_absent():
    results = _results(_EPISODE_MODEL, _rows(_episode_row({"value": "Adolescencia"})))
    result = _by_slot(results, "series_name")[0]
    assert result.status == fv.STATUS_ABSENT
    assert result.type == "SeriesName"
    assert result.type_kind == "CATALOG"
    assert result.mode == "committed"


def test_pinned_comparison_is_normalization_insensitive():
    """Case, NFC accent composition and surrounding whitespace all fold away."""
    types = [_type("Mood", [_entry("Café")])]
    model = _model(
        [_intent("I", ["musica {mood}"], slots=[{"name": "mood", "type": "Mood"}])], types
    )
    pins = ["café", "CAFÉ", "  café  ", "Café"]
    for pin in pins:
        rows = [
            {
                "utterance": "musica algo",
                "expected_intent": "I",
                "expected_slots": {"mood": {"value": pin}},
            }
        ]
        results = _results(model, rows)
        assert _by_slot(results, "mood")[0].status == fv.STATUS_PRESENT, pin


# --- AC 2: any-value rows, contiguous tokens, ja-JP substring --------------


def test_any_value_present_when_entry_occurs_as_tokens():
    types = [_type("SeriesName", [_entry("La Casa de Papel")])]
    model = _model(
        [
            _intent(
                "PlayEpisodeIntent",
                ["reproduce {series_name}"],
                slots=[{"name": "series_name", "type": "SeriesName"}],
            )
        ],
        types,
    )
    rows = [
        {
            "utterance": "reproduce la casa de papel temporada uno",
            "expected_intent": "PlayEpisodeIntent",
            "expected_slots": {"series_name": {}},
        }
    ]
    assert _results(model, rows)[0].status == fv.STATUS_PRESENT


def test_any_value_requires_contiguity():
    types = [_type("SeriesName", [_entry("La Casa de Papel")])]
    model = _model(
        [
            _intent(
                "PlayEpisodeIntent",
                ["reproduce {series_name}"],
                slots=[{"name": "series_name", "type": "SeriesName"}],
            )
        ],
        types,
    )
    rows = [
        {
            "utterance": "reproduce la casa grande de papel",
            "expected_intent": "PlayEpisodeIntent",
            "expected_slots": {"series_name": {}},
        }
    ]
    assert _results(model, rows)[0].status == fv.STATUS_NO_ENTRY_IN_UTTERANCE


def test_any_value_matches_on_word_boundaries_not_substrings():
    """dark is not found inside darker: token sequences, not raw substrings."""
    types = [_type("SeriesName", [_entry("Dark")])]
    model = _model(
        [
            _intent(
                "PlayEpisodeIntent",
                ["reproduce {series_name}"],
                slots=[{"name": "series_name", "type": "SeriesName"}],
            )
        ],
        types,
    )
    darker = [
        {
            "utterance": "reproduce darker temporada uno",
            "expected_intent": "PlayEpisodeIntent",
            "expected_slots": {"series_name": {}},
        }
    ]
    assert _results(model, darker)[0].status == fv.STATUS_NO_ENTRY_IN_UTTERANCE
    exact = [
        {
            "utterance": "reproduce dark temporada uno",
            "expected_intent": "PlayEpisodeIntent",
            "expected_slots": {"series_name": {}},
        }
    ]
    assert _results(model, exact)[0].status == fv.STATUS_PRESENT


def test_ja_jp_uses_substring_containment():
    """ja-JP has no word spaces: 今夜 contains 夜 as a substring and passes."""
    types = [_type("SongTitle", [_entry("夜")])]
    model = _model(
        [
            _intent(
                "PlaySongIntent",
                ["{song} を再生して"],
                slots=[{"name": "song", "type": "SongTitle"}],
            )
        ],
        types,
    )
    rows = [
        {
            "utterance": "今夜 を再生して",
            "expected_intent": "PlaySongIntent",
            "expected_slots": {"song": {}},
        }
    ]
    results = fv.check_fixture_rows(build_effective_model("ja-JP", raw_model=model), rows)
    assert results[0].status == fv.STATUS_PRESENT
    # The same shape in a whitespace-tokenized locale stays token-run based:
    # night inside tonight is NOT a contiguous token match there.
    western = _model(
        [
            _intent(
                "PlaySongIntent",
                ["play {song}"],
                slots=[{"name": "song", "type": "SongTitle"}],
            )
        ],
        [_type("SongTitle", [_entry("night")])],
    )
    western_rows = [
        {
            "utterance": "play tonight",
            "expected_intent": "PlaySongIntent",
            "expected_slots": {"song": {}},
        }
    ]
    assert _results(western, western_rows)[0].status == fv.STATUS_NO_ENTRY_IN_UTTERANCE


# --- AC 3: free-text and builtin types are NOT_APPLICABLE ------------------


def test_free_numeric_structured_slots_are_not_applicable():
    model = _model(
        [
            _intent(
                "I",
                ["buscar {q} n {n} durante {d}"],
                slots=[
                    {"name": "q", "type": "AMAZON.SearchQuery"},
                    {"name": "n", "type": "AMAZON.NUMBER"},
                    {"name": "d", "type": "AMAZON.DURATION"},
                ],
            )
        ],
        [],
    )
    rows = [
        {
            "utterance": "buscar xyzzyfoo n 60 durante PT30M",
            "expected_intent": "I",
            "expected_slots": {"q": {"value": "xyzzyfoo"}, "n": {"value": "60"}, "d": {}},
        }
    ]
    results = _results(model, rows)
    assert all(result.status == fv.STATUS_NOT_APPLICABLE for result in results)
    assert all(result.type_kind == "" for result in results)
    # NOT_APPLICABLE is never a finding in either validator bucket.
    individual, catalog_count = fv.partition_validator_findings(results)
    assert individual == []
    assert catalog_count == 0


# --- AC 4: FIXTURE_SLOT_UNKNOWN and the alternatives rule ------------------


def test_slot_unknown_when_no_alternative_declares_it():
    rows = [_episode_row({}, intent="PlaySongIntent")]
    results = _results(_EPISODE_MODEL, rows)
    result = _by_slot(results, "series_name")[0]
    assert result.status == fv.STATUS_FIXTURE_SLOT_UNKNOWN
    assert result.type == ""
    assert result.type_kind == ""


def test_slot_unknown_when_intent_missing_from_model():
    rows = [_episode_row({}, intent="NoSuchIntent")]
    results = _results(_EPISODE_MODEL, rows)
    assert _by_slot(results, "series_name")[0].status == fv.STATUS_FIXTURE_SLOT_UNKNOWN


def test_alternatives_rule_resolves_from_the_declaring_alternative_only():
    """With A|B where only B declares the slot: no UNKNOWN, B's type wins."""
    model = _model(
        [
            _intent("IntentA", ["camino {other}"], slots=[{"name": "other", "type": "Mood"}]),
            _intent(
                "IntentB",
                ["reproduce {series_name}"],
                slots=[{"name": "series_name", "type": "SeriesName"}],
            ),
        ],
        [SERIES_TYPE, MOOD_TYPE],
    )
    rows = [_episode_row({"value": "Breaking Bad"}, intent="IntentA|IntentB")]
    results = _results(model, rows)
    result = _by_slot(results, "series_name")[0]
    assert result.status == fv.STATUS_PRESENT
    assert result.type == "SeriesName"
    assert result.type_kind == "CATALOG"
    assert result.expected_intent == "IntentA|IntentB"


def test_alternatives_rule_reports_unknown_when_nothing_declares():
    model = _model(
        [
            _intent("IntentA", ["camino {other}"], slots=[{"name": "other", "type": "Mood"}]),
            _intent("IntentB", ["otro camino"]),
        ],
        [MOOD_TYPE],
    )
    rows = [_episode_row({}, intent="IntentA|IntentB")]
    results = _results(model, rows)
    assert _by_slot(results, "series_name")[0].status == fv.STATUS_FIXTURE_SLOT_UNKNOWN


# --- result fields, undeclared CLOSED names, snapshot statuses -------------


def test_undeclared_closed_type_name_keeps_its_kind():
    """A CLOSED type the model does not declare has an empty vocabulary."""
    model = _model(
        [
            _intent(
                "PlayBookIntent",
                ["lee {audiobook}"],
                slots=[{"name": "audiobook", "type": "AudiobookTitle"}],
            )
        ],
        [],
    )
    rows = [
        {
            "utterance": "lee algo",
            "expected_intent": "PlayBookIntent",
            "expected_slots": {"audiobook": {"value": "Un libro"}},
        }
    ]
    result = _results(model, rows)[0]
    assert result.status == fv.STATUS_ABSENT
    assert result.type == "AudiobookTitle"
    assert result.type_kind == "CATALOG"


def test_snapshot_seed_only_status_under_name_level_producer():
    committed = build_effective_model("es-ES", raw_model=_EPISODE_MODEL)
    snapshot = _snapshot({"SeriesName": [{"value": "Adolescencia", "synonyms": []}]})
    effective = build_effective_model("es-ES", raw_model=_EPISODE_MODEL, snapshot=snapshot)
    rows = _rows(_episode_row({"value": "Breaking Bad"}))
    results = fv.check_fixture_rows(
        effective, rows, committed_model=committed, snapshot=snapshot
    )
    result = _by_slot(results, "series_name")[0]
    assert result.status == fv.STATUS_ABSENT_IN_LIBRARY_SEED_ONLY
    assert result.type_kind == "CATALOG"
    assert result.mode == "snapshot"


def test_snapshot_producer_plugin_gives_plain_absent():
    committed = build_effective_model("es-ES", raw_model=_EPISODE_MODEL)
    snapshot = _snapshot(
        {"SeriesName": [{"value": "Adolescencia", "synonyms": []}]}, producer="plugin"
    )
    effective = build_effective_model("es-ES", raw_model=_EPISODE_MODEL, snapshot=snapshot)
    rows = _rows(_episode_row({"value": "Breaking Bad"}))
    results = fv.check_fixture_rows(
        effective, rows, committed_model=committed, snapshot=snapshot
    )
    assert _by_slot(results, "series_name")[0].status == fv.STATUS_ABSENT


def test_snapshot_value_missing_everywhere_is_plain_absent():
    """Missing from BOTH the snapshot and the committed seed: no soft status."""
    committed = build_effective_model("es-ES", raw_model=_EPISODE_MODEL)
    snapshot = _snapshot({"SeriesName": [{"value": "Adolescencia", "synonyms": []}]})
    effective = build_effective_model("es-ES", raw_model=_EPISODE_MODEL, snapshot=snapshot)
    rows = _rows(_episode_row({"value": "Firefly"}))
    results = fv.check_fixture_rows(
        effective, rows, committed_model=committed, snapshot=snapshot
    )
    assert _by_slot(results, "series_name")[0].status == fv.STATUS_ABSENT


def test_snapshot_pinned_value_present_by_snapshot_canonical():
    committed = build_effective_model("es-ES", raw_model=_EPISODE_MODEL)
    snapshot = _snapshot({"SeriesName": [{"value": "Adolescencia", "synonyms": []}]})
    effective = build_effective_model("es-ES", raw_model=_EPISODE_MODEL, snapshot=snapshot)
    rows = _rows(_episode_row({"value": "adolescencia"}))
    results = fv.check_fixture_rows(
        effective, rows, committed_model=committed, snapshot=snapshot
    )
    result = _by_slot(results, "series_name")[0]
    assert result.status == fv.STATUS_PRESENT
    assert result.mode == "snapshot"


def test_unreplaced_type_never_gets_the_seed_only_status():
    """A CATALOG type the snapshot does not name keeps committed values."""
    committed = build_effective_model("es-ES", raw_model=_EPISODE_MODEL)
    snapshot = _snapshot({"SongTitle": [{"value": "Something", "synonyms": []}]})
    effective = build_effective_model("es-ES", raw_model=_EPISODE_MODEL, snapshot=snapshot)
    rows = _rows(_episode_row({"value": "Firefly"}))
    results = fv.check_fixture_rows(
        effective, rows, committed_model=committed, snapshot=snapshot
    )
    assert _by_slot(results, "series_name")[0].status == fv.STATUS_ABSENT


def test_committed_mode_never_yields_the_seed_only_status():
    results = _results(_EPISODE_MODEL, _rows(_episode_row({"value": "Adolescencia"})))
    assert all(
        result.status != fv.STATUS_ABSENT_IN_LIBRARY_SEED_ONLY for result in results
    )


# --- AC 9: the real-data test (committed es-ES MODEL, inline rows) ---------


def test_real_es_es_breaking_bad_inline_row_both_modes():
    """The original incident row: PRESENT committed, seed-only under the snapshot.

    Loads the committed es-ES model (allowed; the parent note names it), never
    the committed fixture file: the rows here are inline literals.
    """
    rows = [
        {
            "utterance": "reproduce breaking bad temporada uno episodio tres",
            "expected_intent": "PlayEpisodeIntent",
            "expected_slots": {
                "series_name": {"value": "breaking bad"},
                "season_number": {},
                "episode_number": {},
            },
        }
    ]
    committed = build_effective_model("es-ES")
    results = fv.check_fixture_rows(committed, rows)
    assert len(results) == 3
    series = _by_slot(results, "series_name")[0]
    assert series.status == fv.STATUS_PRESENT
    assert series.type == "SeriesName"
    assert series.type_kind == "CATALOG"
    assert series.mode == "committed"
    numbers = [result for result in results if result.slot != "series_name"]
    assert all(result.status == fv.STATUS_NOT_APPLICABLE for result in numbers)

    snapshot = _snapshot({"SeriesName": [{"value": "Adolescencia", "synonyms": []}]})
    effective = build_effective_model("es-ES", snapshot=snapshot)
    snapshot_results = fv.check_fixture_rows(
        effective, rows, committed_model=committed, snapshot=snapshot
    )
    snapshot_series = _by_slot(snapshot_results, "series_name")[0]
    assert snapshot_series.status == fv.STATUS_ABSENT_IN_LIBRARY_SEED_ONLY
    assert snapshot_series.type_kind == "CATALOG"
    assert snapshot_series.mode == "snapshot"


def test_real_es_es_adolescencia_inline_row_both_modes():
    """The re-pinned row class: ABSENT committed (CATALOG), PRESENT under the snapshot."""
    rows = [
        {
            "utterance": "reproduce adolescencia temporada uno episodio tres",
            "expected_intent": "PlayEpisodeIntent",
            "expected_slots": {
                "series_name": {"value": "adolescencia"},
                "season_number": {},
                "episode_number": {},
            },
        }
    ]
    committed = build_effective_model("es-ES")
    committed_series = _by_slot(fv.check_fixture_rows(committed, rows), "series_name")[0]
    assert committed_series.status == fv.STATUS_ABSENT
    assert committed_series.type_kind == "CATALOG"

    snapshot = _snapshot({"SeriesName": [{"value": "Adolescencia", "synonyms": []}]})
    effective = build_effective_model("es-ES", snapshot=snapshot)
    snapshot_series = _by_slot(
        fv.check_fixture_rows(effective, rows, committed_model=committed, snapshot=snapshot),
        "series_name",
    )[0]
    assert snapshot_series.status == fv.STATUS_PRESENT


# --- AC 11: the validator partition -----------------------------------------


def _one_result(status: str, type_kind: str) -> fv.FixtureVocabResult:
    return fv.FixtureVocabResult(
        locale="es-ES",
        utterance="u",
        expected_intent="I",
        slot="s",
        type="SeriesName" if type_kind else "T",
        type_kind=type_kind,
        status=status,
        mode="committed",
    )


def test_partition_puts_catalog_dependent_rows_in_the_count_bucket():
    individual, catalog_count = fv.partition_validator_findings(
        [
            _one_result(fv.STATUS_ABSENT, "CATALOG"),
            _one_result(fv.STATUS_NO_ENTRY_IN_UTTERANCE, "CATALOG"),
            _one_result(fv.STATUS_PRESENT, "CATALOG"),
            _one_result(fv.STATUS_NOT_APPLICABLE, ""),
            _one_result(fv.STATUS_ABSENT_IN_LIBRARY_SEED_ONLY, "CATALOG"),
        ]
    )
    assert individual == []
    assert catalog_count == 2


def test_partition_keeps_static_and_unknown_findings_individual():
    individual, catalog_count = fv.partition_validator_findings(
        [
            _one_result(fv.STATUS_ABSENT, "STATIC"),
            _one_result(fv.STATUS_NO_ENTRY_IN_UTTERANCE, "STATIC"),
            _one_result(fv.STATUS_FIXTURE_SLOT_UNKNOWN, ""),
            _one_result(fv.STATUS_PRESENT, "STATIC"),
        ]
    )
    assert catalog_count == 0
    assert [result.status for result in individual] == [
        fv.STATUS_ABSENT,
        fv.STATUS_NO_ENTRY_IN_UTTERANCE,
        fv.STATUS_FIXTURE_SLOT_UNKNOWN,
    ]


# --- ordering, malformed rows, determinism ----------------------------------


def test_results_are_sorted_independently_of_input_order():
    rows = [
        {
            "utterance": "b utterance",
            "expected_intent": "PlaySongIntent",
            "expected_slots": {"song": {}},
        },
        {
            "utterance": "a utterance",
            "expected_intent": "PlayMoodMusicIntent",
            "expected_slots": {"mood": {}, "z": {}},
        },
    ]
    results = _results(_EPISODE_MODEL, rows)
    keys = [result.sort_key() for result in results]
    assert keys == sorted(keys)


def test_malformed_rows_are_skipped():
    rows = [
        "not a dict",
        {"expected_intent": "PlaySongIntent"},
        {"utterance": "no intent", "expected_slots": {"song": {}}},
        {"utterance": "x", "expected_intent": "PlaySongIntent", "expected_slots": "nope"},
        {
            "utterance": "ok pin",
            "expected_intent": "PlayMoodMusicIntent",
            "expected_slots": {"mood": {"value": "calm"}},
        },
    ]
    results = _results(_EPISODE_MODEL, rows)
    assert [result.utterance for result in results] == ["ok pin"]
    assert results[0].status == fv.STATUS_PRESENT


def test_check_is_deterministic_across_runs():
    rows = [
        _episode_row({"value": "Adolescencia"}),
        _episode_row({}),
    ]
    first = _results(_EPISODE_MODEL, rows)
    second = _results(_EPISODE_MODEL, rows)
    assert first == second
    assert [result.sort_key() for result in first] == sorted(
        result.sort_key() for result in first
    )


# --- the validator phase (AC 6, AC 11 wiring) --------------------------------


def _write_fixture(fixtures_dir: Path, locale: str, rows: list[dict]) -> Path:
    path = fixtures_dir / f"{locale}.yaml"
    path.write_text(
        yaml.safe_dump(
            {"locale": locale, "invocation_name": "test", "tests": rows},
            allow_unicode=True,
        ),
        encoding="utf-8",
    )
    return path


def test_validator_phase_reports_static_findings_and_counts_catalog(tmp_path, capsys):
    fixtures_dir = tmp_path / "fixtures"
    fixtures_dir.mkdir()
    _write_fixture(
        fixtures_dir,
        "es-ES",
        [
            # STATIC ABSENT: the pinned mood word is not in the Mood type.
            {
                "utterance": "musica serene",
                "expected_intent": "PlayMoodMusicIntent",
                "expected_slots": {"mood": {"value": "serene"}},
            },
            # CATALOG ABSENT: the adolescencia class, counted not warned.
            _episode_row({"value": "Adolescencia"}),
            # PRESENT: pinned seed value.
            _episode_row({"value": "Breaking Bad"}),
        ],
    )
    warnings = lint_fixture_vocab({"es-ES": _EPISODE_MODEL}, fixtures_dir=fixtures_dir)
    assert warnings is not None
    assert len(warnings) == 1
    assert "ABSENT" in warnings[0] and "Mood" in warnings[0]
    out = capsys.readouterr().out
    assert "catalog-dependent committed-mode fixture rows: 1" in out
    assert "breaking bad" not in out  # the PRESENT row prints nothing


def test_validator_phase_reports_unknown_slot_and_no_entry(tmp_path):
    fixtures_dir = tmp_path / "fixtures"
    fixtures_dir.mkdir()
    _write_fixture(
        fixtures_dir,
        "es-ES",
        [
            {
                "utterance": "musica cualquiera",
                "expected_intent": "PlayMoodMusicIntent",
                "expected_slots": {"mood": {}},
            },
            {
                "utterance": "reproduce breaking bad temporada uno episodio tres",
                "expected_intent": "PlaySongIntent",
                "expected_slots": {"series_name": {}},
            },
        ],
    )
    warnings = lint_fixture_vocab({"es-ES": _EPISODE_MODEL}, fixtures_dir=fixtures_dir)
    assert warnings is not None
    assert len(warnings) == 2
    assert any("NO_ENTRY_IN_UTTERANCE" in warning for warning in warnings)
    assert any("FIXTURE_SLOT_UNKNOWN" in warning for warning in warnings)


def test_validator_phase_skips_when_fixtures_dir_absent(tmp_path, capsys):
    warnings = lint_fixture_vocab(
        {"es-ES": _EPISODE_MODEL}, fixtures_dir=tmp_path / "missing"
    )
    assert warnings is None
    assert "SKIP Phase 12" in capsys.readouterr().out


def test_validator_phase_skips_locale_without_fixture_file(tmp_path, capsys):
    fixtures_dir = tmp_path / "fixtures"
    fixtures_dir.mkdir()
    warnings = lint_fixture_vocab(
        {"es-ES": _EPISODE_MODEL, "fr-FR": _EPISODE_MODEL}, fixtures_dir=fixtures_dir
    )
    assert warnings == []
    assert "SKIP fr-FR: no fixture file" in capsys.readouterr().out


def test_validator_phase_survives_a_broken_fixture(tmp_path):
    fixtures_dir = tmp_path / "fixtures"
    fixtures_dir.mkdir()
    (fixtures_dir / "es-ES.yaml").write_text("tests: [ [ broken", encoding="utf-8")
    warnings = lint_fixture_vocab({"es-ES": _EPISODE_MODEL}, fixtures_dir=fixtures_dir)
    assert warnings == []


# --- the CLI subcommand (AC 5) -----------------------------------------------


def _write_cli_tree(tmp_path: Path) -> tuple[Path, Path]:
    models_dir = tmp_path / "models"
    fixtures_dir = tmp_path / "fixtures"
    models_dir.mkdir()
    fixtures_dir.mkdir()
    (models_dir / "model_es-ES.json").write_text(
        json.dumps(_EPISODE_MODEL, ensure_ascii=False), encoding="utf-8"
    )
    _write_fixture(
        fixtures_dir,
        "es-ES",
        [
            _episode_row(
                {"value": "Adolescencia"}, utterance="reproduce adolescencia temporada dos episodio cuatro"
            ),
            _episode_row({"value": "Breaking Bad"}),
            {
                "utterance": "musica calm",
                "expected_intent": "PlayMoodMusicIntent",
                "expected_slots": {"mood": {"value": "calm"}},
            },
        ],
    )
    return models_dir, fixtures_dir


def test_cli_json_has_exact_fields_and_is_byte_identical(tmp_path, monkeypatch, capsys):
    models_dir, fixtures_dir = _write_cli_tree(tmp_path)
    monkeypatch.setattr(fv, "default_models_dir", lambda: models_dir)
    monkeypatch.setattr(fv, "default_fixtures_dir", lambda: fixtures_dir)
    exit_code = nlu_cli.main(["fixtures", "--locale", "es-ES", "--format", "json"])
    assert exit_code == 0
    first = capsys.readouterr().out
    assert nlu_cli.main(["fixtures", "--locale", "es-ES", "--format", "json"]) == 0
    second = capsys.readouterr().out
    assert first == second
    payload = json.loads(first)
    assert len(payload) == 7  # 2 episode rows x 3 slots + 1 mood row x 1 slot
    for item in payload:
        assert set(item.keys()) == {
            "locale",
            "utterance",
            "expected_intent",
            "slot",
            "type",
            "type_kind",
            "status",
            "mode",
        }
    keys = [
        (item["locale"], item["utterance"], item["expected_intent"], item["slot"], item["status"])
        for item in payload
    ]
    assert keys == sorted(keys)
    by_utterance = {}
    for item in payload:
        by_utterance.setdefault(item["utterance"], {})[item["slot"]] = item["status"]
    assert by_utterance["reproduce breaking bad temporada uno episodio tres"] == {
        "series_name": "PRESENT",
        "season_number": "NOT_APPLICABLE",
        "episode_number": "NOT_APPLICABLE",
    }
    assert by_utterance["musica calm"] == {"mood": "PRESENT"}
    assert by_utterance["reproduce adolescencia temporada dos episodio cuatro"]["series_name"] == "ABSENT"


def test_cli_json_with_snapshot_reports_the_live_question(tmp_path, monkeypatch, capsys):
    models_dir, fixtures_dir = _write_cli_tree(tmp_path)
    monkeypatch.setattr(fv, "default_models_dir", lambda: models_dir)
    monkeypatch.setattr(fv, "default_fixtures_dir", lambda: fixtures_dir)
    snapshot_path = tmp_path / "snapshot.json"
    snapshot_path.write_text(
        json.dumps(_snapshot({"SeriesName": [{"value": "Adolescencia", "synonyms": []}]})),
        encoding="utf-8",
    )
    assert nlu_cli.main(["fixtures", "--locale", "es-ES", "--snapshot", str(snapshot_path)]) == 0
    payload = json.loads(capsys.readouterr().out)
    series = {
        item["utterance"]: (item["status"], item["mode"], item["type_kind"])
        for item in payload
        if item["slot"] == "series_name"
    }
    assert series["reproduce breaking bad temporada uno episodio tres"] == (
        "ABSENT_IN_LIBRARY_SEED_ONLY",
        "snapshot",
        "CATALOG",
    )
    # The snapshot DOES hold the adolescencia row's pin, so it reads PRESENT.
    assert series["reproduce adolescencia temporada dos episodio cuatro"] == (
        "PRESENT",
        "snapshot",
        "CATALOG",
    )


def test_cli_text_mode_prints_findings_and_skips(tmp_path, monkeypatch, capsys):
    models_dir, fixtures_dir = _write_cli_tree(tmp_path)
    monkeypatch.setattr(fv, "default_models_dir", lambda: models_dir)
    monkeypatch.setattr(fv, "default_fixtures_dir", lambda: fixtures_dir)
    assert nlu_cli.main(["fixtures", "--format", "text", "--locale", "es-ES"]) == 0
    out = capsys.readouterr().out
    assert "ABSENT" in out and "SeriesName/CATALOG" in out
    assert nlu_cli.main(["fixtures", "--format", "text", "--locale", "fr-FR"]) == 0
    assert "SKIP fr-FR: no fixture file" in capsys.readouterr().err


def test_cli_refuses_a_broken_snapshot(tmp_path, monkeypatch, capsys):
    models_dir, fixtures_dir = _write_cli_tree(tmp_path)
    monkeypatch.setattr(fv, "default_models_dir", lambda: models_dir)
    monkeypatch.setattr(fv, "default_fixtures_dir", lambda: fixtures_dir)
    bad = tmp_path / "bad.json"
    bad.write_text('{"schema": 99, "locales": {}}', encoding="utf-8")
    assert nlu_cli.main(["fixtures", "--locale", "es-ES", "--snapshot", str(bad)]) == 2


# --- review-driven regressions -----------------------------------------------


def test_name_level_producer_literal_is_pinned_to_the_real_producer():
    """The comment on NAME_LEVEL_PRODUCERS promises this pin: keep the two spellings together."""
    from nlu_static import producer_jellyfin

    assert producer_jellyfin.PRODUCER_NAME == "jellyfin-names"
    assert fv.NAME_LEVEL_PRODUCERS == frozenset({"jellyfin-names"})


def test_partition_never_counts_snapshot_mode_misses_as_catalog_dependent():
    """A snapshot-mode CATALOG miss is an actionable finding, not an expected row."""
    snapshot_result = fv.FixtureVocabResult(
        locale="es-ES",
        utterance="u",
        expected_intent="I",
        slot="s",
        type="SeriesName",
        type_kind="CATALOG",
        status=fv.STATUS_ABSENT,
        mode="snapshot",
    )
    individual, catalog_count = fv.partition_validator_findings([snapshot_result])
    assert catalog_count == 0
    assert individual == [snapshot_result]


def test_mixed_type_slot_keys_do_not_crash():
    """sorted(slots) must not compare int keys to str keys (TypeError)."""
    rows = [
        {
            "utterance": "x",
            "expected_intent": "PlayMoodMusicIntent",
            "expected_slots": {1: {}, "mood": {}},
        }
    ]
    results = _results(_EPISODE_MODEL, rows)
    assert sorted(result.slot for result in results) == ["1", "mood"]


def test_bare_string_expectation_is_a_shorthand_pin():
    rows = [
        {
            "utterance": "musica cualquiera",
            "expected_intent": "PlayMoodMusicIntent",
            "expected_slots": {"mood": "calm"},
        }
    ]
    results = _results(_EPISODE_MODEL, rows)
    assert _by_slot(results, "mood")[0].status == fv.STATUS_PRESENT


def test_unknown_builtin_type_is_reported_not_swallowed(tmp_path):
    """classify_slot_type's contract: the caller reports names the table misses."""
    model = _model(
        [
            _intent(
                "I",
                ["escrito por {author}"],
                slots=[{"name": "author", "type": "AMAZON.BookAuthor"}],
            )
        ],
        [],
    )
    rows = [
        {
            "utterance": "escrito por alguien",
            "expected_intent": "I",
            "expected_slots": {"author": {}},
        }
    ]
    fixtures_dir = tmp_path / "fixtures"
    fixtures_dir.mkdir()
    _write_fixture(fixtures_dir, "es-ES", rows)
    results, skip, unknown = fv.analyze_locale(
        "es-ES", raw_model=model, fixtures_dir=fixtures_dir
    )
    assert skip is None
    assert unknown == ["AMAZON.BookAuthor"]
    assert results[0].status == fv.STATUS_NOT_APPLICABLE

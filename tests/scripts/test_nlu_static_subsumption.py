"""JF-954.4: the sample-subsumption graph, the utterance matcher and the graph diff.

Unit tests compile tiny hand-written patterns rule by rule; the
real-data tests read the committed es-ES model only (per the parent
task's allowance) and pass their fixture rows INLINE, never through the
committed fixture file. No test reaches the network, SMAPI or the ask
CLI; diff-git runs against the local repository's own refs, read-only.
"""

import json
import subprocess
import sys
import time
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from nlu_static import cli as nlu_cli  # noqa: E402
from nlu_static import graph as nlu_graph  # noqa: E402
from nlu_static import matcher as nlu_matcher  # noqa: E402
from nlu_static import patterns  # noqa: E402
from nlu_static.effective_model import (  # noqa: E402
    DEFAULT_MODELS_DIR,
    build_effective_model,
)

COMMITTED_MODELS_DIR = DEFAULT_MODELS_DIR
ES_ES = "es-ES"


# ---------------------------------------------------------------- helpers


def _model(intents, types=None) -> dict:
    """A bare languageModel with the given intents and type blocks."""
    return {
        "invocationName": "test",
        "intents": intents,
        "types": [
            {"name": name, "values": [{"name": {"value": value}} for value in values]}
            for name, values in (types or {}).items()
        ],
    }


def _intent(name, samples, slots=()):
    """One intent block: samples plus (name, type) slot pairs."""
    return {
        "name": name,
        "samples": samples,
        "slots": [{"name": slot_name, "type": slot_type} for slot_name, slot_type in slots],
    }


def _write_and_build(tmp_path, locale, model):
    """Write one synthetic model and build its effective model."""
    path = tmp_path / f"model_{locale}.json"
    path.write_text(json.dumps(model, ensure_ascii=False), encoding="utf-8")
    return build_effective_model(locale, models_dir=tmp_path)


def _compiled(model, intent_name, sample):
    """The CompiledPattern of one sample of one named intent."""
    intent = next(i for i in model.intents if i.name == intent_name)
    return patterns.compile_pattern(sample, intent, model)


def _witness(model, intent_name, sample):
    """The witness of one sample of one named intent."""
    return _compiled(model, intent_name, sample).witness


def _matches_witness(model, sample_intent, sample, witness_intent, witness_sample):
    """Witness-mode match of one sample pattern against another's witness."""
    entry_tokens = patterns.closed_entry_tokens(model)
    return patterns.matches(
        _compiled(model, sample_intent, sample),
        _witness(model, witness_intent, witness_sample),
        entry_tokens,
    )


def _matches_tokens(model, sample_intent, sample, tokens, mode=patterns.UtteranceMode):
    """Match one sample pattern against a plain token tuple."""
    witness = tuple(("lit", token) for token in tokens)
    entry_tokens = patterns.closed_entry_tokens(model)
    return patterns.matches(
        _compiled(model, sample_intent, sample), witness, entry_tokens, mode=mode
    )


# ------------------------------------------------- AC 1: matching rules
# Every rule gets its own match and non-match case, on tiny hand-written
# patterns built through compile_pattern over synthetic models.


def _two_intent_model():
    return _model(
        [
            _intent("PlayXIntent", ["pon {q}", "pon"], [("q", "AMAZON.SearchQuery")]),
            _intent("OtherIntent", ["pon pop", "pon"]),
        ]
    )


def test_rule_literal_match_and_non_match(tmp_path):
    model = _write_and_build(tmp_path, "x-x", _two_intent_model())
    # a pattern literal consumes the identical literal token
    assert _matches_witness(model, "PlayXIntent", "pon", "OtherIntent", "pon")
    # and refuses a different literal at the same position
    assert not _matches_witness(model, "PlayXIntent", "pon", "OtherIntent", "pon pop")


def test_rule_free_one_or_more_match_and_non_match(tmp_path):
    model = _write_and_build(tmp_path, "x-x", _two_intent_model())
    # FREE consumes one token
    assert _matches_witness(model, "PlayXIntent", "pon {q}", "OtherIntent", "pon pop")
    # and several consecutive tokens of any kind (literals here)
    assert _matches_witness(model, "PlayXIntent", "pon {q}", "OtherIntent", "pon pop pop")
    # zero tokens do NOT satisfy a FREE slot
    assert not _matches_witness(model, "PlayXIntent", "pon {q}", "OtherIntent", "pon")


def test_rule_free_consumes_class_tokens(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("SongIntent", ["pon {q}"], [("q", "AMAZON.SearchQuery")]),
                _intent("TimerIntent", ["pon {n} luego"], [("n", "AMAZON.NUMBER")]),
            ]
        ),
    )
    # the witness items a FREE slot consumes may be class tokens too
    assert _matches_witness(model, "SongIntent", "pon {q}", "TimerIntent", "pon {n} luego")


def test_rule_numeric_match_and_non_match(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("TimerIntent", ["espera {n}"], [("n", "AMAZON.NUMBER")]),
                _intent("TwoIntent", ["espera {q} y {r}"], [("q", "AMAZON.SearchQuery"), ("r", "AMAZON.SearchQuery")]),
            ]
        ),
    )
    # NUMERIC accepts the <NUM> class token
    assert _matches_witness(model, "TimerIntent", "espera {n}", "TimerIntent", "espera {n}")
    # witness "espera <FREE> <FREE>": NUMERIC eats exactly one item, the
    # second FREE has nothing left to match, so the whole match fails
    assert not _matches_witness(model, "TimerIntent", "espera {n}", "TwoIntent", "espera {q} y {r}")
    # and the reverse cannot match either: the two-slot pattern's literal
    # "y" finds nothing after the single <NUM> witness item
    assert not _matches_witness(model, "TwoIntent", "espera {q} y {r}", "TimerIntent", "espera {n}")


def test_rule_structured_same_type_match_and_non_match(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("WaitIntent", ["espera {d}"], [("d", "AMAZON.DURATION")]),
                _intent("AlarmIntent", ["espera {t}"], [("t", "AMAZON.TIME")]),
            ]
        ),
    )
    # STRUCTURED accepts the class token of its own type
    assert _matches_witness(model, "WaitIntent", "espera {d}", "WaitIntent", "espera {d}")
    # and refuses another STRUCTURED type's class token
    assert not _matches_witness(model, "WaitIntent", "espera {d}", "AlarmIntent", "espera {t}")
    assert not _matches_witness(model, "AlarmIntent", "espera {t}", "WaitIntent", "espera {d}")


def _closed_model(tmp_path):
    return _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("EpisodeIntent", ["pon {s}"], [("s", "SeriesName")]),
                _intent("MovieIntent", ["pon {m}"], [("m", "MovieTitle")]),
                _intent("LiteralIntent", ["pon breaking bad"]),
                _intent("PartialIntent", ["pon breaking"]),
            ],
            {"SeriesName": ["Breaking Bad"], "MovieTitle": ["Casablanca"]},
        ),
    )


def test_rule_closed_same_type_match_and_non_match(tmp_path):
    model = _closed_model(tmp_path)
    # CLOSED accepts the class token of the same type
    assert _matches_witness(model, "EpisodeIntent", "pon {s}", "EpisodeIntent", "pon {s}")
    # a different CLOSED type with disjoint entries does not match
    assert not _matches_witness(model, "EpisodeIntent", "pon {s}", "MovieIntent", "pon {m}")


def test_rule_closed_intersecting_type_match_and_non_match(tmp_path):
    model = _closed_model(tmp_path)
    entry_tokens = patterns.closed_entry_tokens(model)
    assert not (entry_tokens["SeriesName"] & entry_tokens["MovieTitle"])
    model_intersect = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("EpisodeIntent", ["pon {s}"], [("s", "SeriesName")]),
                _intent("MovieIntent", ["pon {m}"], [("m", "MovieTitle")]),
            ],
            # both types own the identical token tuple ("the", "air", "i",
            # "breathe"): the entry SETS intersect, so the class tokens match
            {"SeriesName": ["The Air I Breathe"], "MovieTitle": ["The Air I Breathe", "Casablanca"]},
        ),
    )
    assert patterns.closed_entry_tokens(model_intersect)["SeriesName"] & patterns.closed_entry_tokens(
        model_intersect
    )["MovieTitle"]
    assert _matches_witness(model_intersect, "EpisodeIntent", "pon {s}", "MovieIntent", "pon {m}")


def test_rule_closed_vs_literal_entry_match_and_non_match(tmp_path):
    model = _closed_model(tmp_path)
    # a CLOSED slot consumes a literal run equal to one entry's token list
    assert _matches_witness(model, "EpisodeIntent", "pon {s}", "LiteralIntent", "pon breaking bad")
    # a partial entry run does not match
    assert not _matches_witness(model, "EpisodeIntent", "pon {s}", "PartialIntent", "pon breaking")


def test_utterance_mode_numeric_and_structured_overapproximate(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("TimerIntent", ["espera {n} segundos"], [("n", "AMAZON.NUMBER")]),
                _intent("WaitIntent", ["espera {d}"], [("d", "AMAZON.DURATION")]),
            ]
        ),
    )
    # utterance mode: NUMERIC stands for exactly one token of any kind
    assert _matches_tokens(model, "TimerIntent", "espera {n} segundos", ("espera", "30", "segundos"))
    assert not _matches_tokens(
        model, "TimerIntent", "espera {n} segundos", ("espera", "mucho", "tiempo", "segundos")
    )
    # STRUCTURED stands for one to four tokens of any kind
    assert _matches_tokens(model, "WaitIntent", "espera {d}", ("espera", "media", "hora"))
    assert _matches_tokens(model, "WaitIntent", "espera {d}", ("espera", "una", "hora", "y", "media"))
    assert not _matches_tokens(
        model, "WaitIntent", "espera {d}", ("espera", "una", "hora", "y", "media", "justa")
    )


# --------------------------------------------- AC 2: IDENTICAL vs SWALLOW


def test_identical_skeletons_make_bidirectional_identical_edges(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("AIntent", ["pon {q}"], [("q", "AMAZON.SearchQuery")]),
                _intent("BIntent", ["pon {r}"], [("r", "AMAZON.SearchQuery")]),
            ]
        ),
    )
    payload = nlu_graph.build_graph("x-x", model)
    edges = {(e["from"], e["to"]): e for e in payload["edges"]}
    assert edges[("AIntent", "BIntent")]["kind"] == "IDENTICAL"
    assert edges[("BIntent", "AIntent")]["kind"] == "IDENTICAL"


def test_free_carrier_makes_one_directional_swallow_edge(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("SongIntent", ["pon {q}"], [("q", "AMAZON.SearchQuery")]),
                _intent(
                    "EpisodeIntent",
                    ["pon {s} temporada {n} episodio {e}"],
                    [("s", "SeriesName"), ("n", "AMAZON.NUMBER"), ("e", "AMAZON.NUMBER")],
                ),
            ],
            {"SeriesName": ["Breaking Bad"]},
        ),
    )
    payload = nlu_graph.build_graph("x-x", model)
    edges = {(e["from"], e["to"]): e for e in payload["edges"]}
    forward = edges[("SongIntent", "EpisodeIntent")]
    assert forward["kind"] == "SWALLOW"
    assert forward["swallowed"] == 1 and forward["total"] == 1
    # the reverse edge cannot exist: the episode pattern cannot parse "pon x"
    assert ("EpisodeIntent", "SongIntent") not in edges


def test_edge_examples_are_first_three_sorted_pairs(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("SongIntent", ["pon {q}"], [("q", "AMAZON.SearchQuery")]),
                _intent("OtherIntent", ["pon bravo", "pon alfa", "pon zulu", "pon mike"]),
            ]
        ),
    )
    payload = nlu_graph.build_graph("x-x", model)
    edge = next(e for e in payload["edges"] if (e["from"], e["to"]) == ("SongIntent", "OtherIntent"))
    assert edge["swallowed"] == 4 and edge["total"] == 4
    assert edge["examples"] == [
        ["pon {q}", "pon alfa"],
        ["pon {q}", "pon bravo"],
        ["pon {q}", "pon mike"],
    ]


def test_universal_intent_lists_bare_carrier(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("CatchAllIntent", ["{q}"], [("q", "AMAZON.SearchQuery")]),
                _intent("AIntent", ["pon alfa"]),
                _intent("BIntent", ["pon bravo"]),
            ]
        ),
    )
    payload = nlu_graph.build_graph("x-x", model)
    assert payload["universal"] == ["CatchAllIntent"]


# ------------------------------------------- AC 3: real es-ES graph edge


def test_es_es_graph_has_pon_song_vs_play_episode_edge():
    model = build_effective_model(ES_ES, models_dir=COMMITTED_MODELS_DIR)
    payload = nlu_graph.build_graph(ES_ES, model)
    edge = next(
        e
        for e in payload["edges"]
        if e["from"] == "PlaySongIntent" and e["to"] == "PlayEpisodeIntent"
    )
    assert edge["kind"] == "SWALLOW"
    assert any(
        a.casefold() == "pon {song}"
        and b == "pon {series_name} temporada {season_number} episodio {episode_number}"
        for a, b in edge["examples"]
    )


# ------------------------------- AC 4: real es-ES fixture-row classes


def test_es_es_inline_breaking_bad_row_is_contested():
    model = build_effective_model(ES_ES, models_dir=COMMITTED_MODELS_DIR)
    verdict = nlu_matcher.classify_fixture_row(
        {
            "utterance": "pon breaking bad temporada uno episodio tres",
            "expected_intent": "PlayEpisodeIntent",
        },
        model,
    )
    assert verdict["classification"] == "CONTESTED"
    assert "PlaySongIntent" in {rival["intent"] for rival in verdict["rivals"]}


def test_es_es_inline_breaking_bad_row_no_sample_match_under_snapshot(tmp_path):
    snapshot = {
        "schema": 1,
        "producer": "test",
        "locales": {"*": {"SeriesName": [{"value": "Adolescencia", "synonyms": []}]}},
    }
    model = build_effective_model(ES_ES, snapshot=snapshot, models_dir=COMMITTED_MODELS_DIR)
    assert model.source == "snapshot"
    verdict = nlu_matcher.classify_fixture_row(
        {
            "utterance": "pon breaking bad temporada uno episodio tres",
            "expected_intent": "PlayEpisodeIntent",
        },
        model,
    )
    assert verdict["classification"] == "NO_SAMPLE_MATCH"
    assert "PlaySongIntent" in {rival["intent"] for rival in verdict["rivals"]}


def test_es_es_inline_adolescencia_row_both_modes():
    committed = build_effective_model(ES_ES, models_dir=COMMITTED_MODELS_DIR)
    row = {
        "utterance": "pon adolescencia temporada uno episodio tres",
        "expected_intent": "PlayEpisodeIntent",
    }
    assert (
        nlu_matcher.classify_fixture_row(dict(row), committed)["classification"]
        == "NO_SAMPLE_MATCH"
    )
    snapshot = {
        "schema": 1,
        "producer": "test",
        "locales": {"*": {"SeriesName": [{"value": "Adolescencia", "synonyms": []}]}},
    }
    snapshot_model = build_effective_model(ES_ES, snapshot=snapshot, models_dir=COMMITTED_MODELS_DIR)
    verdict = nlu_matcher.classify_fixture_row(dict(row), snapshot_model)
    assert verdict["classification"] == "CONTESTED"
    assert "PlaySongIntent" in {rival["intent"] for rival in verdict["rivals"]}


def test_uncontested_row_classifies_clean():
    model = build_effective_model(ES_ES, models_dir=COMMITTED_MODELS_DIR)
    clean = nlu_matcher.classify_fixture_row(
        {"utterance": "Continuar viendo", "expected_intent": "ContinueWatchingIntent"},
        model,
    )
    assert clean["classification"] == "UNCONTESTED"
    assert clean["rivals"] == []


def test_rival_at_least_as_specific_flag(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent(
                    "EpisodeIntent",
                    ["pon {s} temporada {n}"],
                    [("s", "SeriesName"), ("n", "AMAZON.NUMBER")],
                ),
                _intent("SongIntent", ["pon {q}"], [("q", "AMAZON.SearchQuery")]),
            ],
            {"SeriesName": ["Breaking Bad"]},
        ),
    )
    verdict = nlu_matcher.classify_fixture_row(
        {"utterance": "pon breaking bad temporada dos", "expected_intent": "EpisodeIntent"},
        model,
    )
    assert verdict["classification"] == "CONTESTED"
    # the free-text rival (specificity 1) is NOT at least as specific as
    # the two-literal expected sample
    assert verdict["rival_at_least_as_specific"] is False


# ------------------------------------------ AC 5: unlisted AMAZON types


def test_unknown_builtin_type_is_free_and_reported(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent(
                    "CityIntent",
                    ["vai a {city}", "vai a {city} presto"],
                    [("city", "AMAZON.City")],
                ),
                _intent("OtherIntent", ["vai a roma presto"]),
            ]
        ),
    )
    compiled, unclassified = patterns.compile_model_patterns(model)
    assert unclassified == {"AMAZON.City"}
    payload = nlu_graph.build_graph("x-x", model)
    assert payload["unclassified_types"] == ["AMAZON.City"]
    # behaviorally FREE: the slot consumes several literal tokens
    assert _matches_witness(model, "CityIntent", "vai a {city}", "OtherIntent", "vai a roma presto")


def test_match_intents_treats_unknown_builtin_as_free(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("CityIntent", ["vai a {city}"], [("city", "AMAZON.City")]),
                _intent("LiteralIntent", ["vai a x"]),
            ]
        ),
    )
    hits = nlu_matcher.match_intents("vai a santa lucia", model)
    assert [hit["intent"] for hit in hits] == ["CityIntent"]
    assert hits[0]["sample"] == "vai a {city}"


# ------------------------------------- AC 6: determinism, no paths/times


def test_graph_build_byte_identical_and_free_of_paths_and_times(tmp_path):
    out_one = tmp_path / "one"
    out_two = tmp_path / "two"
    assert nlu_cli.main(["graph", "build", "--locale", ES_ES, "--out", str(out_one)]) == 0
    assert nlu_cli.main(["graph", "build", "--locale", ES_ES, "--out", str(out_two)]) == 0
    text_one = (out_one / "graph_es-ES.json").read_text(encoding="utf-8")
    text_two = (out_two / "graph_es-ES.json").read_text(encoding="utf-8")
    assert text_one == text_two
    payload = json.loads(text_one)
    assert payload["schema"] == 1
    assert payload["locale"] == ES_ES
    assert payload["mode"] == "committed"
    assert str(REPO) not in text_one
    assert str(tmp_path) not in text_one
    assert not any(key in payload for key in ("generated", "timestamp", "time", "path"))


# --------------------------------------------- AC 7: graph diff and flags


def _write_graph(tmp_path, name, edges):
    path = tmp_path / name
    path.write_text(
        json.dumps(
            {
                "schema": 1,
                "locale": "x-x",
                "mode": "committed",
                "unclassified_types": [],
                "universal": [],
                "edges": edges,
            }
        ),
        encoding="utf-8",
    )
    return path


def _edge(a, b, kind="SWALLOW", swallowed=1, total=2):
    return {"from": a, "to": b, "kind": kind, "swallowed": swallowed, "total": total, "examples": []}


def test_graph_diff_reports_added_removed_changed_sorted(tmp_path):
    old = _write_graph(tmp_path, "old.json", [_edge("A", "B"), _edge("A", "C", swallowed=2, total=5), _edge("X", "Y")])
    new = _write_graph(tmp_path, "new.json", [_edge("A", "B"), _edge("A", "C", swallowed=3, total=5), _edge("B", "A")])
    lines = nlu_graph.diff_graphs(nlu_graph.load_graph(old), nlu_graph.load_graph(new))
    # the diff groups added, removed and changed, each group sorted
    assert lines == [
        "added B -> A kind=SWALLOW swallowed=1/2",
        "removed X -> Y kind=SWALLOW swallowed=1/2",
        "changed A -> C kind=SWALLOW->SWALLOW swallowed=2/5->3/5",
    ]


def test_graph_diff_reports_changed_total(tmp_path):
    # competition that resized without changing swallowed (the template
    # added samples A still does not match) must still be reported
    old = _write_graph(tmp_path, "old.json", [_edge("A", "B", swallowed=2, total=5)])
    new = _write_graph(tmp_path, "new.json", [_edge("A", "B", swallowed=2, total=6)])
    lines = nlu_graph.diff_graphs(nlu_graph.load_graph(old), nlu_graph.load_graph(new))
    assert lines == ["changed A -> B kind=SWALLOW->SWALLOW swallowed=2/5->2/6"]


def test_graph_diff_fail_on_new_exit_codes(tmp_path, capsys):
    old = _write_graph(tmp_path, "old.json", [_edge("A", "B")])
    added = _write_graph(tmp_path, "added.json", [_edge("A", "B"), _edge("C", "D")])
    changed = _write_graph(tmp_path, "changed.json", [_edge("A", "B", swallowed=2, total=2)])
    same = _write_graph(tmp_path, "same.json", [_edge("A", "B")])
    assert nlu_cli.main(["graph", "diff", str(old), str(added), "--fail-on-new"]) == 1
    assert nlu_cli.main(["graph", "diff", str(old), str(changed), "--fail-on-new"]) == 0
    assert nlu_cli.main(["graph", "diff", str(old), str(same), "--fail-on-new"]) == 0
    capsys.readouterr()


def test_graph_diff_git_empty_against_head(capsys):
    # AC 8 automated anchor: the working tree's es-ES model equals HEAD's,
    # so the reviewer command must print an empty diff and exit 0. The
    # manual full run is recorded in the task notes.
    rc = nlu_cli.main(["graph", "diff-git", "--base", "HEAD", "--locale", ES_ES])
    captured = capsys.readouterr()
    assert rc == 0
    assert captured.out == ""


def test_graph_diff_git_real_ref_detects_a_model_change():
    # The parent of the newest es-ES model commit (the JF-958 genre-carrier
    # qualification) removes real PlayByGenreIntent edges against today's tree.
    log = subprocess.run(
        [
            "git",
            "log",
            "--format=%H",
            "-1",
            "--",
            "Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_es-ES.json",
        ],
        capture_output=True,
        text=True,
        check=True,
        cwd=REPO,
    )
    newest = log.stdout.strip()
    assert newest
    proc = subprocess.run(
        [
            sys.executable,
            str(SCRIPTS_DIR / "nlu_static" / "cli.py"),
            "graph",
            "diff-git",
            "--base",
            f"{newest}~1",
            "--locale",
            ES_ES,
        ],
        capture_output=True,
        text=True,
        check=False,
        cwd=REPO,
    )
    assert proc.returncode == 0
    assert "== es-ES ==" in proc.stdout
    assert "removed PlayByGenreIntent" in proc.stdout


def test_graph_diff_git_fail_on_new_stays_zero_against_head(capsys):
    rc = nlu_cli.main(["graph", "diff-git", "--base", "HEAD", "--locale", ES_ES, "--fail-on-new"])
    capsys.readouterr()
    assert rc == 0


# ---------------------------------------------------- AC 9: SKIP lines


def test_skip_lines_on_every_command(tmp_path, capsys):
    out_dir = tmp_path / "graphs"
    rc_build = nlu_cli.main(["graph", "build", "--locale", "ja-JP", "--out", str(out_dir)])
    rc_match = nlu_cli.main(["match", "--locale", "ja-JP", "x"])
    rc_diff_git = nlu_cli.main(["graph", "diff-git", "--base", "HEAD", "--locale", "ja-JP"])
    captured = capsys.readouterr()
    expected = "SKIP ja-JP: no whitespace tokenization"
    assert rc_build == rc_match == rc_diff_git == 0
    assert captured.out.count(expected) == 3
    assert not (out_dir / "graph_ja-JP.json").exists()
    rc_classify = nlu_cli.main(["classify-fixtures", "--locale", "ja-JP"])
    captured = capsys.readouterr()
    assert rc_classify == 2
    assert expected in captured.err


def test_skip_line_in_all_locale_run(tmp_path, capsys):
    assert nlu_cli.main(["graph", "build", "--all", "--out", str(tmp_path)]) == 0
    captured = capsys.readouterr()
    assert "SKIP ja-JP: no whitespace tokenization" in captured.out
    assert not (tmp_path / "graph_ja-JP.json").exists()
    assert (tmp_path / "graph_es-ES.json").exists()


# ------------------------------------------------- AC 10: full-build time


def test_full_build_under_120_seconds(tmp_path):
    """AC 10, executed: the 16 tokenized locales must build in under 120s."""
    start = time.monotonic()
    assert nlu_cli.main(["graph", "build", "--all", "--out", str(tmp_path)]) == 0
    elapsed = time.monotonic() - start
    assert elapsed < 120.0, f"full graph build took {elapsed:.1f}s"
    graphs = sorted(p.name for p in tmp_path.glob("graph_*.json"))
    assert len(graphs) == 16


# ------------------------------------------------- matcher unit behavior


def test_match_intents_ranking_and_specificity(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent("SpecificIntent", ["pon el disco {q} de rock"], [("q", "AMAZON.SearchQuery")]),
                _intent("BroadIntent", ["pon {q}"], [("q", "AMAZON.SearchQuery")]),
            ]
        ),
    )
    hits = nlu_matcher.match_intents("pon el disco abc de rock", model)
    assert [(hit["intent"], hit["specificity"]) for hit in hits] == [
        ("SpecificIntent", 5),
        ("BroadIntent", 1),
    ]
    assert hits[0]["sample"] == "pon el disco {q} de rock"


def test_match_intents_empty_utterance(tmp_path):
    model = _write_and_build(
        tmp_path, "x-x", _model([_intent("AIntent", ["pon {q}"], [("q", "AMAZON.SearchQuery")])])
    )
    # punctuation-only text tokenizes to nothing: no hits
    assert nlu_matcher.match_intents("!!!", model) == []


def test_classify_fixture_row_accepts_alternatives(tmp_path):
    model = _write_and_build(
        tmp_path, "x-x", _model([_intent("AIntent", ["pon {q}"], [("q", "AMAZON.SearchQuery")])])
    )
    verdict = nlu_matcher.classify_fixture_row(
        {"utterance": "pon alfa", "expected_intent": "MissingIntent|AIntent"}, model
    )
    assert verdict["classification"] == "UNCONTESTED"
    assert verdict["rivals"] == []
    assert verdict["rival_at_least_as_specific"] is False


def test_classify_payload_field_names(tmp_path):
    model = _write_and_build(
        tmp_path,
        "x-x",
        _model(
            [
                _intent(
                    "EpisodeIntent",
                    ["pon {s} temporada {n}"],
                    [("s", "SeriesName"), ("n", "AMAZON.NUMBER")],
                ),
                _intent("SongIntent", ["pon {q}"], [("q", "AMAZON.SearchQuery")]),
            ],
            {"SeriesName": ["Breaking Bad"]},
        ),
    )
    verdict = nlu_matcher.classify_fixture_row(
        {"utterance": "pon breaking bad temporada dos", "expected_intent": "EpisodeIntent"},
        model,
    )
    assert set(verdict) == {"classification", "rivals", "rival_at_least_as_specific"}
    assert set(verdict["rivals"][0]) == {"intent", "sample", "specificity"}


# --------------------------------------- classify-fixtures CLI contract


def test_classify_fixtures_counts_malformed_rows(tmp_path, capsys, monkeypatch):
    fixtures_dir = tmp_path / "fixtures"
    fixtures_dir.mkdir()
    (fixtures_dir / "es-ES.yaml").write_text(
        "tests:\n"
        "  - utterance: pon breaking bad temporada uno episodio tres\n"
        "    expected_intent: PlayEpisodeIntent\n"
        "  - utterance: null\n"
        "    expected_intent: PlayEpisodeIntent\n"
        "  - utterance: continuar viendo\n"
        "    expected_intent: null\n",
        encoding="utf-8",
    )
    monkeypatch.setattr(nlu_cli.fixture_vocab, "default_fixtures_dir", lambda: fixtures_dir)
    out = tmp_path / "classify.json"
    rc = nlu_cli.main(["classify-fixtures", "--locale", ES_ES, "--out", str(out)])
    captured = capsys.readouterr()
    assert rc == 0
    rows = json.loads(out.read_text(encoding="utf-8"))
    # only the well-formed row reaches the JSON
    assert [row["utterance"] for row in rows] == ["pon breaking bad temporada uno episodio tres"]
    # and the two poisoned rows are reported explicitly, not silently dropped
    assert "note [es-ES]: 2 fixture row(s) without a string utterance or expected_intent were skipped" in captured.err


def test_graph_build_requires_locale_selection(capsys):
    # neither --locale nor --all: argparse refuses with its own clear
    # message instead of the misleading --out complaint
    with pytest.raises(SystemExit) as excinfo:
        nlu_cli.main(["graph", "build", "--out", "/tmp/nowhere"])
    assert excinfo.value.code == 2
    assert "--locale" in capsys.readouterr().err


def test_graph_build_all_continues_after_bad_locale(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(
        nlu_cli.fixture_vocab, "committed_locales", lambda: ["zz-ZZ", ES_ES]
    )
    rc = nlu_cli.main(["graph", "build", "--all", "--out", str(tmp_path)])
    captured = capsys.readouterr()
    assert rc == 1
    # the unreadable locale is refused with a clean one-liner...
    assert "error:" in captured.err and "zz-ZZ" in captured.err
    # ...and the healthy locale still produced its graph
    assert (tmp_path / "graph_es-ES.json").exists()


def test_classify_fixtures_cli_es_es(tmp_path):
    out = tmp_path / "classify.json"
    assert nlu_cli.main(["classify-fixtures", "--locale", ES_ES, "--out", str(out)]) == 0
    rows = json.loads(out.read_text(encoding="utf-8"))
    assert rows
    for row in rows:
        assert set(row) == {
            "locale",
            "utterance",
            "expected_intent",
            "classification",
            "rivals",
            "rival_at_least_as_specific",
        }
    assert rows == sorted(rows, key=lambda r: (r["locale"], r["utterance"], r["expected_intent"]))
    classes = {row["classification"] for row in rows}
    assert classes <= {"UNCONTESTED", "CONTESTED", "NO_SAMPLE_MATCH"}

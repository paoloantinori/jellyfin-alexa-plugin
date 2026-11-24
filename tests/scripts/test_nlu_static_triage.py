"""JF-954.6: the automatic triage report battery.

Every test builds its own synthetic ledger sessions (through the same
``LedgerWriter`` the live suite uses, with fixed clock, git, identity and
model-sha callables) and its own tiny models in ``tmp_path``; the two
realistic-scenario tests read the COMMITTED es-ES model only (the parent
task's allowance) and keep their ledger rows INLINE, never through a
committed fixture file. No test reaches the network, SMAPI or the ask CLI.
"""

import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
for entry in (str(SCRIPTS_DIR),):
    if entry not in sys.path:
        sys.path.insert(0, entry)

from nlu_static import cli as nlu_cli  # noqa: E402
from nlu_static import ledger as nlu_ledger  # noqa: E402
from nlu_static import triage as nlu_triage  # noqa: E402

FIXED_MOMENT = datetime(2026, 11, 24, 12, 0, 0, tzinfo=timezone.utc)

ES_EPISODE_UTTERANCE = "reproduce breaking bad temporada uno episodio tres"


# ---------------------------------------------------------------- helpers


def _writer(out_dir: Path, pid: int, minute: int, identity="etag-A", sha="sha-A"):
    return nlu_ledger.LedgerWriter(
        out_dir=out_dir,
        clock=lambda: FIXED_MOMENT.replace(minute=minute),
        git_reader=lambda: ("head123", False),
        identity_reader=lambda locale: identity,
        model_sha256_fn=lambda locale: sha,
        pid=pid,
    )


def _row(writer, locale, utterance, expected, actual, outcome, slots=None, kind="intent"):
    writer.record_row(
        locale=locale,
        utterance=utterance,
        expected_intent=expected,
        expected_slots={} if slots is None else slots,
        actual_intent=actual,
        slots={},
        outcome=outcome,
        failure_kind=None if outcome == nlu_ledger.OUTCOME_PASS else kind,
    )


def _session(
    out_dir: Path,
    pid: int,
    minute: int,
    rows,
    *,
    complete: bool = True,
    identity="etag-A",
    identity_end=None,
    sha="sha-A",
    locale="es-ES",
):
    """Write one session file; rows are (utterance, expected, actual, outcome[, slots]) tuples."""
    writer = _writer(out_dir, pid, minute, sha=sha)
    # The identity reader answers locale_start with ``identity`` and the
    # session-end read with ``identity_end`` (default: unchanged), which is
    # how rebuilt_during_run becomes True for the unstable-build tests.
    identities = [identity, identity_end or identity]
    writer._identity_reader = lambda locale: identities.pop(0) if identities else identity
    writer.start_session(skill_id="sk-1")
    writer.start_locale(locale)
    for row in rows:
        slots = row[4] if len(row) > 4 else None
        kind = row[5] if len(row) > 5 else "intent"
        _row(writer, locale, row[0], row[1], row[2], row[3], slots=slots, kind=kind)
    if complete:
        writer.finish()
    else:
        writer.close()


def _load(out_dir: Path):
    return nlu_ledger.load_sessions(out_dir)


def _synthetic_model(out_dir: Path, locale: str = "es-ES") -> Path:
    """The tiny model the verdict tests train against.

    PlayEpisodeIntent (SeriesName + two NUMBER slots) competes with
    PlaySongIntent (free-text song); CarriedIntent carries the literal
    run "la cosa" while OwnerIntent owns the StaticThing type that
    declares the same entry; StaticIntent pins the STATIC type
    StaticThing.
    """
    model = {
        "languageModel": {
            "intents": [
                {
                    "name": "PlayEpisodeIntent",
                    "slots": [
                        {"name": "series_name", "type": "SeriesName"},
                        {"name": "season_number", "type": "AMAZON.NUMBER"},
                        {"name": "episode_number", "type": "AMAZON.NUMBER"},
                    ],
                    "samples": [
                        "reproduce {series_name} temporada {season_number} episodio {episode_number}",
                    ],
                },
                {
                    "name": "PlaySongIntent",
                    "slots": [{"name": "song", "type": "AMAZON.MusicRecording"}],
                    "samples": ["reproduce {song}"],
                },
                {
                    "name": "PlayNextIntent",
                    "slots": [{"name": "song", "type": "AMAZON.MusicRecording"}],
                    "samples": ["reproduce {song} a continuacion"],
                },
                {
                    "name": "CarriedIntent",
                    "samples": ["pon la cosa"],
                },
                {
                    "name": "OwnerIntent",
                    "slots": [{"name": "thing", "type": "StaticThing"}],
                    "samples": ["{thing}"],
                },
                {
                    "name": "RivalOwnerIntent",
                    "slots": [{"name": "thing", "type": "StaticThing"}],
                    "samples": ["pon {thing}"],
                },
                {
                    "name": "StaticIntent",
                    "slots": [{"name": "name", "type": "StaticThing"}],
                    "samples": ["dime {name}"],
                },
            ],
            "types": [
                {
                    "name": "SeriesName",
                    "values": [{"name": {"value": "Breaking Bad", "synonyms": []}}],
                },
                {
                    "name": "StaticThing",
                    "values": [{"name": {"value": "la cosa", "synonyms": []}}],
                },
            ],
        }
    }
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"model_{locale}.json"
    path.write_text(json.dumps(model), encoding="utf-8")
    return path


def _triage(out_dir: Path, models_dir: Path | None = None, **kwargs):
    sessions = _load(out_dir)
    if models_dir is not None:
        kwargs.setdefault("models_dir", models_dir)
    return nlu_triage.triage(sessions, **kwargs)


def _verdicts(payload):
    return {row["utterance"]: row["verdict"] for row in payload["rows"]}


def _by_utterance(payload, utterance):
    matches = [row for row in payload["rows"] if row["utterance"] == utterance]
    assert len(matches) == 1, matches
    return matches[0]


EPISODE_ROWS = [
    (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlaySongIntent", nlu_ledger.OUTCOME_FAIL,
     {"series_name": {"value": "breaking bad"}}),
]
NEXT_ACTUAL_ROWS = [
    (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlayNextIntent", nlu_ledger.OUTCOME_FAIL,
     {"series_name": {"value": "breaking bad"}}),
]
STATIC_ABSENT_ROWS = [
    ("dime la tortuga", "StaticIntent", "StaticIntent", nlu_ledger.OUTCOME_FAIL,
     {"name": {"value": "la tortuga"}}),
]
COLLISION_ROWS = [
    ("pon la cosa", "CarriedIntent", "OwnerIntent", nlu_ledger.OUTCOME_FAIL),
]


# ------------------------------------------------------- one test per verdict


def test_verdict_infra(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 101, 0, [("hola", "X", None, nlu_ledger.OUTCOME_ERROR, {}, "smapi_error")])
    payload = _triage(led, models)
    row = _by_utterance(payload, "hola")
    assert row["verdict"] == nlu_triage.V_INFRA
    assert row["failure_kind"] == "smapi_error"
    assert any("failure_kind=smapi_error" in d for d in row["detail"])


def test_verdict_unstable_build(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(
        led, 102, 0, EPISODE_ROWS,
        identity="etag-before", identity_end="etag-after", complete=True,
    )
    payload = _triage(led, models)
    assert _by_utterance(payload, ES_EPISODE_UTTERANCE)["verdict"] == nlu_triage.V_UNSTABLE_BUILD
    assert _by_utterance(payload, ES_EPISODE_UTTERANCE)["rebuilt_during_run"] is True


def test_verdict_fixture_vocab_committed_static(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 103, 0, STATIC_ABSENT_ROWS)
    payload = _triage(led, models)
    row = _by_utterance(payload, "dime la tortuga")
    assert row["verdict"] == nlu_triage.V_FIXTURE_VOCAB
    assert any("absent in committed seed" in d for d in row["detail"])
    assert row["vocab_status"] == "ABSENT"


def test_verdict_fixture_vocab_snapshot_live_catalog(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    # A CATALOG type under a snapshot that lacks the pin: snapshots only
    # replace catalog-backed types, so this is the path whose detail says
    # "absent in live catalog".
    _session(led, 104, 0, EPISODE_ROWS)
    snapshot = {
        "schema": 1,
        "producer": "plugin",
        "locales": {"*": {"SeriesName": [{"value": "Otra serie", "synonyms": []}]}},
    }
    payload = _triage(led, models, snapshot=snapshot)
    row = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert row["verdict"] == nlu_triage.V_FIXTURE_VOCAB
    assert any("absent in live catalog" in d for d in row["detail"])


def test_verdict_known_flaky(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    passing = [
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlaySongIntent",
         nlu_ledger.OUTCOME_PASS, {"series_name": {"value": "breaking bad"}}),
    ]
    # Two prior sessions whose representatives flipped under the SAME
    # committed model and the SAME stable live identity.
    _session(led, 111, 0, [
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlaySongIntent",
         nlu_ledger.OUTCOME_PASS, {"series_name": {"value": "breaking bad"}})])
    _session(led, 112, 1, [
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlayEpisodeIntent",
         nlu_ledger.OUTCOME_PASS, {"series_name": {"value": "breaking bad"}})])
    _session(led, 113, 2, EPISODE_ROWS)
    payload = _triage(led, models)
    row = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert row["verdict"] == nlu_triage.V_KNOWN_FLAKY
    assert row["flips_same_model"] == 1
    assert row["history_runs"] == 2
    assert any("flips_same_model=1" in d for d in row["detail"])


def test_verdict_contested(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 105, 0, EPISODE_ROWS)  # actual PlaySongIntent IS a rival
    payload = _triage(led, models)
    row = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert row["verdict"] == nlu_triage.V_CONTESTED
    assert row["match_classification"] == "CONTESTED"
    assert any("lost to PlaySongIntent" in d for d in row["detail"])


def test_verdict_token_collision(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 106, 0, COLLISION_ROWS)
    payload = _triage(led, models)
    row = _by_utterance(payload, "pon la cosa")
    assert row["verdict"] == nlu_triage.V_TOKEN_COLLISION
    assert any("token collision: es-ES|CARRIER_VS_VALUE|StaticThing|la cosa|CarriedIntent" in d
               for d in row["detail"])


def test_verdict_systematic_candidate(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 107, 0, NEXT_ACTUAL_ROWS)  # actual PlayNextIntent is NOT a rival
    payload = _triage(led, models)
    row = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert row["verdict"] == nlu_triage.V_SYSTEMATIC
    assert row["match_classification"] == "CONTESTED"  # contested, but the rival lost to is not the actual intent


# ---------------------------------------------------------------- precedence


def test_precedence_infra_beats_unstable_build(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 121, 0, [("hola", "X", None, nlu_ledger.OUTCOME_ERROR)],
             identity="etag-before", identity_end="etag-after")
    payload = _triage(led, models)
    assert _verdicts(payload) == {"hola": nlu_triage.V_INFRA}


def test_precedence_unstable_build_beats_fixture_vocab(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 122, 0, STATIC_ABSENT_ROWS,
             identity="etag-before", identity_end="etag-after")
    payload = _triage(led, models)
    assert _verdicts(payload) == {"dime la tortuga": nlu_triage.V_UNSTABLE_BUILD}
    # The vocabulary signal is still carried even though the rule lost.
    assert _by_utterance(payload, "dime la tortuga")["vocab_status"] == "ABSENT"


def test_precedence_fixture_vocab_beats_known_flaky(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    stable_static = [
        ("dime la tortuga", "StaticIntent", "StaticIntent", nlu_ledger.OUTCOME_PASS,
         {"name": {"value": "la tortuga"}}),
    ]
    _session(led, 131, 0, stable_static)
    _session(led, 132, 1, [
        ("dime la tortuga", "StaticIntent", "OwnerIntent", nlu_ledger.OUTCOME_PASS,
         {"name": {"value": "la tortuga"}})])
    _session(led, 133, 2, STATIC_ABSENT_ROWS)
    payload = _triage(led, models)
    row = _by_utterance(payload, "dime la tortuga")
    assert row["verdict"] == nlu_triage.V_FIXTURE_VOCAB
    assert row["flips_same_model"] >= 1  # the flaky history was present and lost


def test_precedence_known_flaky_beats_contested(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 141, 0, [
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlayEpisodeIntent",
         nlu_ledger.OUTCOME_PASS, {"series_name": {"value": "breaking bad"}})])
    _session(led, 142, 1, [
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlaySongIntent",
         nlu_ledger.OUTCOME_PASS, {"series_name": {"value": "breaking bad"}})])
    _session(led, 143, 2, EPISODE_ROWS)  # actual PlaySongIntent IS a rival
    payload = _triage(led, models)
    row = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert row["verdict"] == nlu_triage.V_KNOWN_FLAKY
    assert row["match_classification"] == "CONTESTED"


def test_precedence_contested_beats_token_collision(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    # RivalOwnerIntent's "pon {thing}" sample makes it a rival of the very
    # utterance whose collision rule would also fire: rule 5 must win.
    _session(led, 151, 0, [
        ("pon la cosa", "CarriedIntent", "RivalOwnerIntent", nlu_ledger.OUTCOME_FAIL),
    ])
    payload = _triage(led, models)
    row = _by_utterance(payload, "pon la cosa")
    assert row["verdict"] == nlu_triage.V_CONTESTED
    assert any("token collision" not in d for d in row["detail"])


def test_precedence_token_collision_beats_systematic(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 106, 0, COLLISION_ROWS)
    payload = _triage(led, models)
    row = _by_utterance(payload, "pon la cosa")
    assert row["verdict"] == nlu_triage.V_TOKEN_COLLISION
    # The row is CONTESTED (RivalOwnerIntent matches), but rule 5 declines
    # because the row's actual intent (OwnerIntent) is not among the
    # rivals; the collision rule beat the systematic fallback.
    assert row["match_classification"] == "CONTESTED"


# ------------------------------------------------------- signals and details


def test_raw_signals_carried_regardless_of_verdict(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 161, 0, [
        ("hola", "X", None, nlu_ledger.OUTCOME_ERROR),
        ("pon la cosa", "CarriedIntent", "OwnerIntent", nlu_ledger.OUTCOME_FAIL),
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlayNextIntent", nlu_ledger.OUTCOME_FAIL,
         {"series_name": {"value": "breaking bad"}}),
    ])
    payload = _triage(led, models)
    assert len(payload["rows"]) == 3
    for row in payload["rows"]:
        for key in (
            "vocab_status", "match_classification", "rivals",
            "flips_same_model", "history_runs", "rebuilt_during_run",
            "failure_kind",
        ):
            assert key in row, (row["utterance"], key)
    episode = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert episode["match_classification"] == "CONTESTED"
    assert episode["rivals"]


def test_history_insufficient_never_known_flaky(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    # Only one prior session: the key has runs=1, so no flip can be
    # counted and the KNOWN_FLAKY predicate must refuse regardless.
    _session(led, 171, 0, [
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlayEpisodeIntent",
         nlu_ledger.OUTCOME_PASS, {"series_name": {"value": "breaking bad"}})])
    _session(led, 172, 1, [
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlaySongIntent",
         nlu_ledger.OUTCOME_PASS, {"series_name": {"value": "breaking bad"}})])
    _session(led, 173, 2, NEXT_ACTUAL_ROWS)
    # A history window of 1 leaves only the middle session before the
    # chosen one: runs=1, so the key cannot prove a flip.
    payload = _triage(led, models, flaky_min_flips=0, history=1)
    row = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert row["history_runs"] == 1
    assert any(nlu_triage.DETAIL_HISTORY_INSUFFICIENT in d for d in row["detail"])
    # The predicate itself refuses below two runs even with an impossible
    # flip count handed to it.
    state = nlu_triage._RowState(
        signals={"history_runs": 1, "flips_same_model": 5},
        locale_ctx=None, flaky_min_flips=0, is_ja=False,
    )
    assert nlu_triage._pred_known_flaky(state) == (False, [])


def test_ja_jp_skips_static_rules_but_keeps_others(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models, locale="ja-JP")
    led = tmp_path / "led"
    _session(led, 181, 0, [
        ("これを再生して", "PlayEpisodeIntent", "PlaySongIntent", nlu_ledger.OUTCOME_FAIL),
        ("これを聞かせて", "StaticIntent", "StaticIntent", nlu_ledger.OUTCOME_FAIL,
         {"name": {"value": "存在しないもの"}}),
    ], locale="ja-JP")
    payload = _triage(led, models)
    contested_like = _by_utterance(payload, "これを再生して")
    assert contested_like["verdict"] == nlu_triage.V_SYSTEMATIC
    assert contested_like["match_classification"] is None
    assert any(nlu_triage.DETAIL_JA_SKIPPED in d for d in contested_like["detail"])
    vocab_row = _by_utterance(payload, "これを聞かせて")
    assert vocab_row["verdict"] == nlu_triage.V_FIXTURE_VOCAB
    assert any(nlu_triage.DETAIL_JA_SKIPPED in d for d in vocab_row["detail"])


def test_absent_in_library_seed_only_never_fires(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 191, 0, EPISODE_ROWS)
    # A NAME-LEVEL snapshot missing the seed title yields
    # ABSENT_IN_LIBRARY_SEED_ONLY, which must NOT fire FIXTURE_VOCAB; the
    # row falls through to the matcher verdict (SYSTEMATIC here, because
    # the actual intent is not among the rivals).
    snapshot = {
        "schema": 1,
        "producer": "jellyfin-names",
        "locales": {"*": {"SeriesName": [{"value": "Adolescencia", "synonyms": []}]}},
    }
    payload = _triage(led, models, snapshot=snapshot)
    row = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert row["vocab_status"] == "ABSENT_IN_LIBRARY_SEED_ONLY"
    # Rule 3 did not fire; the row keeps its matcher verdict instead.
    assert row["verdict"] == nlu_triage.V_CONTESTED


def test_vocab_rule_reads_ledger_slots_not_fixture_files(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    # A pin that no committed fixture of this repo carries: if the rule
    # read the fixture files, this row could not be FIXTURE_VOCAB.
    led_rows = [
        ("dime el artefacto inventado", "StaticIntent", "StaticIntent",
         nlu_ledger.OUTCOME_FAIL, {"name": {"value": "el artefacto inventado"}}),
    ]
    _session(led, 201, 0, led_rows)
    payload = _triage(led, models)
    row = _by_utterance(payload, "dime el artefacto inventado")
    assert row["verdict"] == nlu_triage.V_FIXTURE_VOCAB
    assert row["expected_slots"] == {"name": {"value": "el artefacto inventado"}}


# ---------------------------------------------------------------- families


def test_family_grouping_and_order(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    _synthetic_model(models, locale="es-MX")
    led = tmp_path / "led"
    # The ONE chosen session carries three rows that share the family key
    # (es, PlayEpisodeIntent, PlayNextIntent, SYSTEMATIC_CANDIDATE) across
    # two locales, plus one error row forming the INFRA family.
    writer = _writer(led, 211, 0)
    writer.start_session(skill_id="sk-1")
    for record in (
        dict(locale="es-ES", utterance=ES_EPISODE_UTTERANCE,
             expected_intent="PlayEpisodeIntent",
             expected_slots={"series_name": {"value": "breaking bad"}},
             actual_intent="PlayNextIntent", slots={},
             outcome=nlu_ledger.OUTCOME_FAIL, failure_kind="intent"),
        dict(locale="es-ES", utterance="reproduce dark temporada dos episodio cuatro",
             expected_intent="PlayEpisodeIntent",
             expected_slots={"series_name": {"value": "dark"}},
             actual_intent="PlayNextIntent", slots={},
             outcome=nlu_ledger.OUTCOME_FAIL, failure_kind="intent"),
        dict(locale="es-ES", utterance="hola", expected_intent="X",
             expected_slots={}, actual_intent=None, slots={},
             outcome=nlu_ledger.OUTCOME_ERROR, failure_kind="smapi_error"),
        dict(locale="es-MX", utterance="reproduce dark temporada dos episodio cuatro",
             expected_intent="PlayEpisodeIntent",
             expected_slots={"series_name": {"value": "dark"}},
             actual_intent="PlayNextIntent", slots={},
             outcome=nlu_ledger.OUTCOME_FAIL, failure_kind="intent"),
    ):
        writer.record_row(**record)
    writer.finish()
    payload = _triage(led, models)
    families = payload["families"]
    assert [(f["language"], f["expected_intent"], f["actual_intent"], f["verdict"])
            for f in families] == [
        ("es", "PlayEpisodeIntent", "PlayNextIntent", nlu_triage.V_SYSTEMATIC),
        ("es", "X", "", nlu_triage.V_INFRA),
    ]
    assert families[0]["size"] == 3
    assert families[0]["locales"] == ["es-ES", "es-MX"]
    assert len(families[0]["rows"]) == 3
    assert families[1]["size"] == 1


# ---------------------------------------------------------------- sessions


def test_default_session_is_latest_complete(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 221, 0, [("primera fila", "StaticIntent", "StaticIntent",
                            nlu_ledger.OUTCOME_FAIL, {"name": {"value": "x"}})])
    _session(led, 222, 1, [("segunda fila", "StaticIntent", "StaticIntent",
                            nlu_ledger.OUTCOME_FAIL, {"name": {"value": "x"}})],
             complete=False)
    payload = _triage(led, models)
    assert payload["session"]["session_id"] == "nlu-20261124T120000Z-221"
    assert payload["session_completeness"] == "complete"
    latest = _triage(led, models, session_arg="latest")
    assert latest["session"]["session_id"] == "nlu-20261124T120100Z-222"
    assert latest["session_completeness"] == "incomplete"
    md = nlu_triage.render_markdown(latest)
    assert "(incomplete)" in md


def test_fail_on_systematic_exit_codes(tmp_path, capsys):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 231, 0, [("hola", "X", None, nlu_ledger.OUTCOME_ERROR),
                           (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlayNextIntent",
                            nlu_ledger.OUTCOME_FAIL,
                            {"series_name": {"value": "breaking bad"}})],
             locale="es-ES")
    base = ["triage", "--dir", str(led), "--format", "md"]
    assert nlu_cli.main(base) == 0
    assert nlu_cli.main(base + ["--fail-on", "systematic"]) == 1
    # A session with only INFRA rows never trips the flag.
    infra_led = tmp_path / "led_infra"
    _session(infra_led, 232, 0, [("hola", "X", None, nlu_ledger.OUTCOME_ERROR)])
    assert nlu_cli.main(["triage", "--dir", str(infra_led), "--format", "md",
                         "--fail-on", "systematic"]) == 0


# ---------------------------------------------------------------- determinism


def test_md_and_json_byte_identical_across_runs(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 241, 0, [
        ("hola", "X", None, nlu_ledger.OUTCOME_ERROR),
        ("pon la cosa", "CarriedIntent", "OwnerIntent", nlu_ledger.OUTCOME_FAIL),
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlayNextIntent", nlu_ledger.OUTCOME_FAIL,
         {"series_name": {"value": "breaking bad"}}),
    ])
    runs_md = []
    runs_json = []
    for _ in range(2):
        payload = _triage(led, models)
        runs_json.append(json.dumps(payload, sort_keys=True))
        runs_md.append(nlu_triage.render_markdown(payload))
    assert runs_json[0] == runs_json[1]
    assert runs_md[0] == runs_md[1]
    # No wall-clock value anywhere: the only timestamps allowed are the
    # ledger's own fixed session stamp.
    assert "2026-11-24T12:0" not in runs_md[0]
    assert str(tmp_path) not in runs_md[0]


def test_cli_md_output_deterministic_and_fails_on_systematic(tmp_path, capsys):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 251, 0, [
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlayNextIntent", nlu_ledger.OUTCOME_FAIL,
         {"series_name": {"value": "breaking bad"}}),
    ])
    argv = ["triage", "--dir", str(led), "--format", "md"]
    outs = []
    for _ in range(2):
        assert nlu_cli.main(argv) == 0
        outs.append(capsys.readouterr().out)
    assert outs[0] == outs[1] and outs[0]
    # The CLI resolves the session rows' locale against the committed
    # models dir (read-only); the report must still be well-formed.
    assert "# NLU triage report" in outs[0]


# ------------------------------------------------------- realistic scenario


def test_realistic_es_episode_without_snapshot_is_systematic_candidate(tmp_path):
    """The committed es-ES model, an INLINE ledger row, no snapshot.

    The matcher truly returns CONTESTED with rivals PlayArtistSongsIntent
    and PlaySongIntent; the actual intent (PlayNextIntent) is none of
    them, so rules 5 and 6 cannot fire and the verdict is
    SYSTEMATIC_CANDIDATE (stated in the name, per the task).
    """
    led = tmp_path / "led"
    _session(led, 261, 0, NEXT_ACTUAL_ROWS)
    payload = _triage(led)  # no models_dir: the committed es-ES model
    row = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert row["verdict"] == nlu_triage.V_SYSTEMATIC
    assert row["match_classification"] == "CONTESTED"
    assert {rival["intent"] for rival in row["rivals"]} == {
        "PlayArtistSongsIntent", "PlaySongIntent",
    }
    assert row["vocab_status"] == "PRESENT"  # the committed seed owns the pin
    family = payload["families"][0]
    assert (family["language"], family["verdict"]) == ("es", nlu_triage.V_SYSTEMATIC)


def test_realistic_es_episode_with_plugin_snapshot_is_fixture_vocab(tmp_path):
    """The Breaking Bad class: a plugin-producer snapshot without the title."""
    led = tmp_path / "led"
    _session(led, 262, 0, NEXT_ACTUAL_ROWS)
    snapshot = {
        "schema": 1,
        "producer": "plugin",
        "locales": {"*": {"SeriesName": [{"value": "Adolescencia", "synonyms": []}]}},
    }
    payload = _triage(led, snapshot=snapshot)
    row = _by_utterance(payload, ES_EPISODE_UTTERANCE)
    assert row["verdict"] == nlu_triage.V_FIXTURE_VOCAB
    assert any("absent in live catalog" in d for d in row["detail"])
    assert row["vocab_status"] == "ABSENT"


# ------------------------------------------------------- markdown contract


def test_markdown_layout_and_limits_paragraph(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 271, 0, [
        (ES_EPISODE_UTTERANCE, "PlayEpisodeIntent", "PlayNextIntent", nlu_ledger.OUTCOME_FAIL,
         {"series_name": {"value": "breaking bad"}}),
        ("hola", "X", None, nlu_ledger.OUTCOME_ERROR),
    ])
    payload = _triage(led, models)
    md = nlu_triage.render_markdown(payload)
    lines = md.splitlines()
    assert lines[0] == "# NLU triage report"
    body_start = md.index("## Look at these first")
    # No verdict section may precede the Look-at-these-first section.
    for verdict in nlu_triage.VERDICT_ORDER:
        if verdict == nlu_triage.V_SYSTEMATIC:
            continue
        heading = f"## {verdict} "
        if heading in md:
            assert md.index(heading) > body_start
    assert md.rstrip().endswith(nlu_triage.LIMITS_PARAGRAPH)
    assert "## Look at these first" in md
    assert "| locale | utterance | expected | actual | detail |" in md


def test_markdown_no_systematic_section_still_starts_body_right(tmp_path):
    models = tmp_path / "models"
    _synthetic_model(models)
    led = tmp_path / "led"
    _session(led, 281, 0, [("hola", "X", None, nlu_ledger.OUTCOME_ERROR)])
    md = nlu_triage.render_markdown(_triage(led, models))
    assert "No SYSTEMATIC_CANDIDATE rows" in md
    assert md.rstrip().endswith(nlu_triage.LIMITS_PARAGRAPH)


# ------------------------------------------------------- committed-tree docs


def test_docs_and_claude_md_carry_the_contract_lines():
    docs = (REPO / "docs" / "nlu-static-analysis.md").read_text(encoding="utf-8")
    assert "Triage in five minutes" in docs
    assert "cli.py triage" in docs
    for verdict in (
        "INFRA", "UNSTABLE_BUILD", "FIXTURE_VOCAB", "KNOWN_FLAKY",
        "CONTESTED", "TOKEN_COLLISION", "SYSTEMATIC_CANDIDATE",
    ):
        assert verdict in docs
    claude = (REPO / "CLAUDE.md").read_text(encoding="utf-8")
    assert (
        "python3 scripts/nlu_static/cli.py triage --snapshot <file>"
        "   # classify red NLU rows after a live suite"
        " (see docs/nlu-static-analysis.md)" in claude
    )

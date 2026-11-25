"""JF-970: the carriers subcommand (does a candidate sample already exist, and where).

Real-data tests read the committed en-US model (the task's worked
example); synthetic-model tests follow the subsumption suite's
conventions (tiny hand-written models written to tmp_path). No test
reaches the network, SMAPI or the ask CLI.
"""

import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from nlu_static import carriers  # noqa: E402
from nlu_static import cli as nlu_cli  # noqa: E402
from nlu_static.effective_model import build_effective_model  # noqa: E402

EN_US = "en-US"


# ---------------------------------------------------------------- helpers


def _run(capsys, argv):
    """Run the CLI's carriers subcommand and return the parsed JSON rows."""
    exit_code = nlu_cli.main(["carriers", "--format", "json"] + argv)
    assert exit_code == 0
    return json.loads(capsys.readouterr().out)


def _en_us_model():
    return build_effective_model(EN_US)


def _synthetic_model(tmp_path, intents, types=None):
    """One synthetic locale model written to tmp_path (subsumption convention)."""
    model = {
        "invocationName": "test",
        "intents": [
            {
                "name": name,
                "samples": samples,
                "slots": [
                    {"name": slot_name, "type": slot_type}
                    for slot_name, slot_type in slots
                ],
            }
            for name, samples, slots in intents
        ],
        "types": [
            {"name": name, "values": [{"name": {"value": value}} for value in values]}
            for name, values in (types or {}).items()
        ],
    }
    path = tmp_path / f"model_x-x.json"
    path.write_text(json.dumps(model, ensure_ascii=False), encoding="utf-8")
    return build_effective_model("x-x", models_dir=tmp_path)


# --------------------------------------------- AC 1: the en-US worked case


def test_ac1_play_some_genre_reports_same_carrier_with_jellyfin_artist_owner(capsys):
    """'play some {genre}' on PlayByGenreIntent shares its carrier with
    PlayArtistSongsIntent's 'play some {musician}' (slot type JellyfinArtist),
    which match cannot show: it only lists catalog-slotted hits whose spoken
    value is in the vocabulary."""
    # Sanity: the committed en-US model genuinely carries the owner sample.
    model = _en_us_model()
    artist = next(i for i in model.intents if i.name == "PlayArtistSongsIntent")
    assert "play some {musician}" in artist.samples
    # ...and the owning slot is the catalog-backed JellyfinArtist type.
    assert any(
        slot.name == "musician" and slot.type == "JellyfinArtist" for slot in artist.slots
    )

    rows = _run(
        capsys,
        ["--locale", EN_US, "--intent", "PlayByGenreIntent", "--sample", "play some {genre}"],
    )
    assert len(rows) == 1
    row = rows[0]
    assert row["verdict"] == "SAME_CARRIER"
    assert row["locale"] == EN_US
    assert row["intent"] == "PlayByGenreIntent"
    assert row["sample"] == "play some {genre}"
    assert row["present"] is None
    assert len(row["same_carrier"]) == 1
    hit = row["same_carrier"][0]
    assert hit["intent"] == "PlayArtistSongsIntent"
    assert hit["sample"] == "play some {musician}"
    assert hit["slots"] == [{"name": "musician", "type": "JellyfinArtist", "catalog": True}]


# ----------------------------------------------------------- AC 2: PRESENT


def test_present_on_named_intent_exact_sample(capsys):
    """A candidate identical to an existing sample of the named intent is PRESENT."""
    rows = _run(
        capsys,
        [
            "--locale",
            EN_US,
            "--intent",
            "PlayArtistSongsIntent",
            "--sample",
            "play {musician} songs",
        ],
    )
    assert rows[0]["verdict"] == "PRESENT"
    assert rows[0]["present"]["intent"] == "PlayArtistSongsIntent"
    assert rows[0]["present"]["sample"] == "play {musician} songs"


def test_present_on_named_intent_same_carrier_different_slot_name():
    """An existing sample of the named intent with the identical carrier but a
    different slot NAME is still PRESENT (the carrier erases slot names)."""
    model = build_effective_model(
        "x-x",
        raw_model={
            "intents": [
                {
                    "name": "TargetIntent",
                    "samples": ["play {genre}"],
                    "slots": [{"name": "genre", "type": "AMAZON.Genre"}],
                }
            ],
            "types": [],
        },
    )
    row = carriers.analyze_candidate(
        "x-x", "TargetIntent", "play {artist}", carriers.build_index("x-x", model)
    )
    assert row["verdict"] == "PRESENT"
    assert row["present"]["sample"] == "play {genre}"
    assert row["same_carrier"] == []


# ------------------------------------------------------------ AC 3: ABSENT


def test_absent_carrier(capsys):
    """An unseen carrier reports ABSENT with empty evidence everywhere."""
    rows = _run(
        capsys,
        [
            "--locale",
            EN_US,
            "--intent",
            "PlayByGenreIntent",
            "--sample",
            "frobnicate the {widget} gently",
        ],
    )
    assert rows[0]["verdict"] == "ABSENT"
    assert rows[0]["present"] is None
    assert rows[0]["same_carrier"] == []


def test_absent_with_punctuation_and_case_folds_to_seen_carrier_shape():
    """Punctuation and case fold away per the shared normalizer: a candidate
    that differs only there is NOT absent when the clean shape exists."""
    model = build_effective_model(
        "x-x",
        raw_model={
            "intents": [
                {
                    "name": "A",
                    "samples": ["pon pop"],
                    "slots": [],
                }
            ],
            "types": [],
        },
    )
    row = carriers.analyze_candidate(
        "x-x", "B", "Pon, POP!", carriers.build_index("x-x", model)
    )
    assert row["verdict"] == "SAME_CARRIER"
    assert row["same_carrier"][0]["sample"] == "pon pop"


# ------------------------------- AC 4: file input, --all, determinism


def test_file_rows_and_all_expansion_deterministic(tmp_path, capsys):
    """A TSV candidate file plus a JSON file produce one row per (candidate,
    locale), candidates in input order, locales sorted, identical across runs."""
    tsv = tmp_path / "candidates.tsv"
    tsv.write_text(
        "# locale\tintent\tsample\n"
        "en-US\tPlayByGenreIntent\tplay some {genre}\n"
        "\n"
        "es-ES\t\tplay {genre}\n"
        "*\tPlaySongIntent\tplay {song}\n",
        encoding="utf-8",
    )
    argv = ["--file", str(tsv)]
    first = _run(capsys, argv)
    second = _run(capsys, argv)
    assert first == second

    # candidate 1: one named locale; candidate 2: one named locale; candidate
    # 3: the '*' locale expands to every committed locale in sorted order.
    from nlu_static import fixture_vocab  # noqa: E402

    committed = fixture_vocab.committed_locales()
    assert len(first) == 2 + len(committed)
    assert [r["locale"] for r in first[:2]] == ["en-US", "es-ES"]
    assert [r["locale"] for r in first[2:]] == committed
    assert all(r["intent"] == "PlaySongIntent" for r in first[2:])
    # the '*' row is present on its named intent in en-US (PlaySongIntent
    # owns 'play {song}'), so the expansion answers PRESENT there.
    song_row = next(r for r in first[2:] if r["locale"] == "en-US")
    assert song_row["verdict"] == "PRESENT"
    assert song_row["present"]["sample"] == "play {song}"

    # A .json file is not a candidate format anymore (TSV is the one form):
    # parsed as TSV lines it refuses with the column error, exit 2.
    json_file = tmp_path / "candidates.json"
    json_file.write_text(
        json.dumps([{"locale": "en-US", "intent": "PlayByGenreIntent", "sample": "play some {genre}"}]),
        encoding="utf-8",
    )
    exit_code = nlu_cli.main(["carriers", "--format", "json", "--file", str(json_file)])
    assert exit_code == 2


def test_inline_all_expansion_covers_every_committed_locale(capsys):
    """--all with one inline candidate enumerates one row per committed locale."""
    from nlu_static import fixture_vocab  # noqa: E402

    committed = fixture_vocab.committed_locales()
    rows = _run(capsys, ["--all", "--intent", "PlayByGenreIntent", "--sample", "zz qqq {qq}"])
    assert [r["locale"] for r in rows] == committed
    assert all(r["verdict"] == "ABSENT" for r in rows)


# ----------------------------------- AC 5: normalization equivalence shape


def test_slot_name_differences_reduce_to_same_carrier(tmp_path):
    """'play {genre}' vs 'play {artist}' is the SAME carrier by design (AC 5):
    every placeholder reduces to one anonymous token."""
    assert carriers.carrier_key("play {genre}", "en-US") == carriers.carrier_key(
        "play {artist}", "en-US"
    )
    model = _synthetic_model(
        tmp_path,
        [
            ("AIntent", ["play {genre}"], [("genre", "AMAZON.Genre")]),
            ("BIntent", ["play {artist}"], [("artist", "JellyfinArtist")]),
        ],
        types={"JellyfinArtist": ["someone"]},
    )
    row = carriers.analyze_candidate(
        "x-x", "AIntent", "play {artist}", carriers.build_index("x-x", model)
    )
    assert row["verdict"] == "PRESENT"  # named intent's own identical carrier
    # The same candidate WITHOUT a named intent reports the cross-intent owner.
    row = carriers.analyze_candidate(
        "x-x", None, "play {artist}", carriers.build_index("x-x", model)
    )
    assert row["verdict"] == "SAME_CARRIER"
    hit = row["same_carrier"][0]
    assert hit["intent"] == "AIntent"
    assert hit["slots"] == [{"name": "genre", "type": "AMAZON.Genre", "catalog": False}]


def test_multi_placeholder_carriers_need_identical_slot_positions(tmp_path):
    """Two slots at different positions are different carriers; identical
    positions with different names are the same carrier."""
    assert carriers.carrier_key("play {a} by {b}", "en-US") != carriers.carrier_key(
        "{a} by {b}", "en-US"
    )
    assert carriers.carrier_key("play {a} by {b}", "en-US") == carriers.carrier_key(
        "play {x} by {y}", "en-US"
    )


# ------------------------------------------------------------ ja-JP rule


def test_jajp_character_level_carrier():
    """ja-JP has no word spaces: the carrier is the character-level reduced
    text, so two samples differing only in slots still share one carrier."""
    assert carriers.carrier_key("曲を再生して", "ja-JP") == ("曲を再生して",)
    assert carriers.carrier_key("{a}を再生して", "ja-JP") == ("‹slot›を再生して",)
    assert carriers.carrier_key("{a}を再生して", "ja-JP") == carriers.carrier_key(
        "{b} を再生して", "ja-JP"
    )


# ------------------------------------------------------------ refusals


def test_inline_sample_without_locale_or_all_is_refused(capsys):
    exit_code = nlu_cli.main(["carriers", "--sample", "play {genre}"])
    assert exit_code == 2
    assert "need --locale" in capsys.readouterr().err


def test_unknown_locale_is_refused(capsys):
    exit_code = nlu_cli.main(
        ["carriers", "--locale", "xx-XX", "--sample", "play {genre}"]
    )
    assert exit_code == 2
    assert "error" in capsys.readouterr().err

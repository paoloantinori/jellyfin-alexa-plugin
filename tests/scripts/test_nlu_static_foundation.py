"""JF-954.1: the nlu_static foundation battery.

Covers the shared normalizer, the effective-model builder, the catalog
snapshot format and the two CLI subcommands. Synthetic models are written
into tmp_path; the only real-data touches are the committed es-ES model
(read only, per the parent task's allowance) and the CatalogSlotTypes.cs
grep. No test reaches the network, SMAPI, the ask CLI or Jellyfin: the
producer's HTTP seam is replaced with a fake everywhere.
"""

import json
import re
import sys
import urllib.parse
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from nlu_static import cli as nlu_cli  # noqa: E402
from nlu_static import producer_jellyfin  # noqa: E402
from nlu_static.effective_model import (  # noqa: E402
    CATALOG_TYPE_NAMES,
    DEFAULT_MODELS_DIR,
    build_effective_model,
    classify_slot_type,
    load_locale_model,
    load_snapshot,
)
from nlu_static.normalize import (  # noqa: E402
    is_whitespace_tokenized,
    normalize_text,
    skip_line,
    tokenize,
)

COMMITTED_MODELS_DIR = DEFAULT_MODELS_DIR
CS_PATH = REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "Catalog" / "CatalogSlotTypes.cs"


def _write_model(tmp_path: Path, locale: str, model: dict) -> Path:
    """Write one synthetic bare languageModel document for a locale."""
    path = tmp_path / f"model_{locale}.json"
    path.write_text(json.dumps(model, ensure_ascii=False), encoding="utf-8")
    return path


def _series_model(types: dict[str, list[str]]) -> dict:
    """A bare languageModel with one type block per given name -> values."""
    return {
        "invocationName": "test",
        "intents": [],
        "types": [
            {"name": name, "values": [{"name": {"value": value}} for value in values]}
            for name, values in types.items()
        ],
    }


def _snapshot(locale_table: dict) -> dict:
    return {"schema": 1, "producer": "test", "locales": locale_table}


# --- AC 1: normalize_text and tokenize -------------------------------------------------


def test_normalize_text_nfc_casefold_and_whitespace_collapse():
    assert normalize_text("  Café   BAR \t\n ") == "café bar"
    assert normalize_text("A\tB\n\nC") == "a b c"
    assert normalize_text("") == ""
    assert normalize_text("   ") == ""


def test_normalize_text_accented_and_decomposed_inputs_normalize_equal():
    # The AC's pair: one precomposed accented input, one decomposed one.
    assert normalize_text("CAFÉ") == normalize_text("café")
    assert normalize_text("CAFÉ") == "café"
    # Casefold is stronger than lower: sharp s folds to ss.
    assert normalize_text("STRASSE") == normalize_text("straße")


def test_tokenize_strips_edge_punctuation_keeps_inner_apostrophes_and_hyphens():
    assert tokenize("l'album") == ["l'album"]
    assert tokenize("schlaf-timer") == ["schlaf-timer"]
    assert tokenize("  (Suona) \"Dark\",;.") == ["suona", "dark"]
    assert tokenize("!!!") == []
    assert tokenize("'tis the season") == ["tis", "the", "season"]
    assert tokenize("") == []


def test_tokenize_collapses_accents_through_nfc_first():
    assert tokenize("CAFÉ, Résumé.") == ["café", "résumé"]


# --- AC 2: is_whitespace_tokenized -----------------------------------------------------


def test_is_whitespace_tokenized_false_only_for_ja_jp():
    model_files = sorted(COMMITTED_MODELS_DIR.glob("model_*.json"))
    assert model_files, "committed models dir must contain model_*.json files"
    locales = [path.name[len("model_") : -len(".json")] for path in model_files]
    assert len(locales) == 17
    for locale in locales:
        if locale == "ja-JP":
            assert is_whitespace_tokenized(locale) is False, locale
        else:
            assert is_whitespace_tokenized(locale) is True, locale


def test_is_whitespace_tokenized_explicit_members():
    assert is_whitespace_tokenized("ja-JP") is False
    assert is_whitespace_tokenized("it-IT") is True
    assert is_whitespace_tokenized("en-US") is True


# --- load_locale_model -----------------------------------------------------------------


def test_load_locale_model_accepts_bare_language_model_document(tmp_path):
    model = _series_model({"SeriesName": ["Alpha"]})
    _write_model(tmp_path, "zz-XX", {"languageModel": model})
    loaded = load_locale_model("zz-XX", models_dir=tmp_path)
    assert loaded["invocationName"] == "test"


def test_load_locale_model_accepts_interaction_model_wrapper(tmp_path):
    model = _series_model({"SeriesName": ["Alpha"]})
    _write_model(tmp_path, "zy-XX", {"interactionModel": {"languageModel": model}})
    loaded = load_locale_model("zy-XX", models_dir=tmp_path)
    assert loaded["invocationName"] == "test"


def test_load_locale_model_rejects_junk_and_missing_files(tmp_path):
    _write_model(tmp_path, "zx-XX", {"unrelated": True})
    with pytest.raises(ValueError, match="zx-XX"):
        load_locale_model("zx-XX", models_dir=tmp_path)
    with pytest.raises(FileNotFoundError):
        load_locale_model("no-such-locale", models_dir=tmp_path)


# --- AC 3: build_effective_model on the committed es-ES model --------------------------


def test_committed_es_es_seriesname_is_catalog_with_breaking_bad():
    model = build_effective_model("es-ES")
    assert model.source == "committed"
    assert model.snapshot_types == ()
    assert model.notes == ()
    vocab = model.type_vocab("SeriesName")
    assert vocab is not None
    assert vocab.kind == "CATALOG"
    canonicals = [entry[0] for entry in vocab.entries]
    assert "Breaking Bad" in canonicals


def test_es_es_snapshot_without_breaking_bad_removes_it():
    snapshot = _snapshot(
        {"*": {"SeriesName": [{"value": "Adolescencia", "synonyms": []}]}}
    )
    model = build_effective_model("es-ES", snapshot=snapshot)
    assert model.source == "snapshot"
    assert model.snapshot_types == ("SeriesName",)
    vocab = model.type_vocab("SeriesName")
    assert vocab is not None
    assert vocab.kind == "CATALOG"  # kind follows the type, not the value source
    canonicals = [entry[0] for entry in vocab.entries]
    assert canonicals == ["Adolescencia"]
    assert "Breaking Bad" not in canonicals


# --- AC 4: "*" fallback and per-locale override ----------------------------------------


def test_star_locale_applies_and_per_locale_entry_overrides_it(tmp_path):
    for locale in ("xx-XX", "yy-YY"):
        _write_model(tmp_path, locale, _series_model({"SeriesName": ["Seed"]}))
    snapshot = _snapshot(
        {
            "*": {"SeriesName": [{"value": "From Star", "synonyms": []}]},
            "xx-XX": {"SeriesName": [{"value": "From Locale", "synonyms": []}]},
        }
    )
    xx = build_effective_model("xx-XX", snapshot=snapshot, models_dir=tmp_path)
    yy = build_effective_model("yy-YY", snapshot=snapshot, models_dir=tmp_path)
    assert xx.type_vocab("SeriesName").entries == (("From Locale", ()),)
    assert yy.type_vocab("SeriesName").entries == (("From Star", ()),)
    assert xx.source == "snapshot"
    assert yy.source == "snapshot"


def test_static_types_keep_committed_values_and_snapshot_types_merge(tmp_path):
    _write_model(
        tmp_path,
        "xx-XX",
        _series_model({"SeriesName": ["Seed"], "Mood": ["Relaxed"]}),
    )
    snapshot = _snapshot(
        {"xx-XX": {"SeriesName": [{"value": "Replaced", "synonyms": []}]}}
    )
    model = build_effective_model("xx-XX", snapshot=snapshot, models_dir=tmp_path)
    assert model.type_vocab("Mood").kind == "STATIC"
    assert model.type_vocab("Mood").entries == (("Relaxed", ()),)
    assert model.type_vocab("SeriesName").entries == (("Replaced", ()),)
    assert model.snapshot_types == ("SeriesName",)


def test_unknown_snapshot_type_is_ignored_and_noted(tmp_path):
    _write_model(tmp_path, "xx-XX", _series_model({"SeriesName": ["Seed"]}))
    snapshot = _snapshot({"xx-XX": {"NotDeclared": [{"value": "x", "synonyms": []}]}})
    model = build_effective_model("xx-XX", snapshot=snapshot, models_dir=tmp_path)
    assert model.type_vocab("NotDeclared") is None
    assert model.snapshot_types == ()
    assert model.source == "committed"  # nothing was replaced, so values are committed
    assert model.notes == ("snapshot type 'NotDeclared' is not declared by the xx-XX model; ignored",)


def test_snapshot_without_matching_locale_notes_and_keeps_committed(tmp_path):
    _write_model(tmp_path, "xx-XX", _series_model({"SeriesName": ["Seed"]}))
    snapshot = _snapshot({"other-LL": {"SeriesName": [{"value": "x", "synonyms": []}]}})
    model = build_effective_model("xx-XX", snapshot=snapshot, models_dir=tmp_path)
    assert model.type_vocab("SeriesName").entries == (("Seed", ()),)
    assert model.source == "committed"
    assert len(model.notes) == 1
    assert "no entry for locale xx-XX" in model.notes[0]


# --- AC 5: load_snapshot validation ----------------------------------------------------


def _write_snapshot(tmp_path: Path, doc) -> Path:
    path = tmp_path / "snapshot.json"
    if isinstance(doc, str):
        path.write_text(doc, encoding="utf-8")
    else:
        path.write_text(json.dumps(doc), encoding="utf-8")
    return path


def test_load_snapshot_accepts_valid_file(tmp_path):
    path = _write_snapshot(
        tmp_path,
        _snapshot({"es-ES": {"SeriesName": [{"value": "Dark", "synonyms": ["dark"]}]}}),
    )
    doc = load_snapshot(path)
    assert doc["producer"] == "test"


# --- AC 5: load_snapshot rejections ----------------------------------------------------
# One parametrized body: every case must raise ValueError naming the file.

_SNAPSHOT_REJECTION_CASES = [
    pytest.param({"schema": 2, "locales": {"es-ES": {}}}, ["schema"], id="wrong-schema"),
    pytest.param({"producer": "x", "locales": {"es-ES": {}}}, [], id="missing-schema"),
    pytest.param(_snapshot({"es-ES": {"SeriesName": "Breaking Bad"}}), ["SeriesName"], id="non-list-type-entry"),
    pytest.param(_snapshot({"es-ES": {"SeriesName": [{"value": 7}, {"novalue": "x"}]}}), ["string 'value'"], id="entry-without-string-value"),
    pytest.param(_snapshot({"es-ES": {"SeriesName": [{"value": "x", "synonyms": [1]}]}}), [], id="non-string-synonyms"),
    pytest.param("{not json", [], id="invalid-json"),
    pytest.param("[]", [], id="bad-top-level"),
]


@pytest.mark.parametrize("document,fragments", _SNAPSHOT_REJECTION_CASES)
def test_load_snapshot_rejects_naming_path(tmp_path, document, fragments):
    path = _write_snapshot(tmp_path, document)
    with pytest.raises(ValueError) as excinfo:
        load_snapshot(path)
    message = str(excinfo.value)
    assert str(path) in message
    for fragment in fragments:
        assert fragment in message


# --- AC 6: classify_slot_type ----------------------------------------------------------


def test_classify_slot_type_pinned_table():
    model = build_effective_model("es-ES")
    expected = {
        "AMAZON.SearchQuery": "FREE",
        "AMAZON.MusicRecording": "FREE",
        "AMAZON.Musician": "FREE",
        "AMAZON.Genre": "FREE",
        "AMAZON.Person": "FREE",
        "AMAZON.NUMBER": "NUMERIC",
        "AMAZON.DURATION": "STRUCTURED",
        "AMAZON.TIME": "STRUCTURED",
        "SeriesName": "CLOSED",  # declared CATALOG custom type
        "Mood": "CLOSED",  # declared STATIC custom type
        "AMAZON.Flight": "UNKNOWN_BUILTIN",  # unlisted AMAZON type
    }
    for type_name, want in expected.items():
        assert classify_slot_type(type_name, model) == want, type_name


def test_classify_slot_type_unknown_builtin_not_hidden_by_declaration(tmp_path):
    _write_model(tmp_path, "xx-XX", _series_model({"AMAZON.Weird": ["x"]}))
    model = build_effective_model("xx-XX", models_dir=tmp_path)
    assert classify_slot_type("AMAZON.Weird", model) == "UNKNOWN_BUILTIN"


# --- AC 7: CATALOG_TYPE_NAMES vs CatalogSlotTypes.cs -----------------------------------


def _catalog_slot_type_names_from_csharp() -> set[str]:
    """Extract the CatalogSlotTypeNames values from the C# source of truth."""
    text = CS_PATH.read_text(encoding="utf-8")
    match = re.search(
        r"CatalogSlotTypeNames = new\(\)\s*\{(.*?)\n    \};", text, re.DOTALL
    )
    assert match is not None, "CatalogSlotTypeNames initializer not found in CatalogSlotTypes.cs"
    block = match.group(1)
    return set(re.findall(r'\[CatalogType\.\w+\]\s*=\s*"(\w+)"', block))


def test_catalog_type_names_match_csharp():
    from_csharp = _catalog_slot_type_names_from_csharp()
    from_python = set(CATALOG_TYPE_NAMES)
    assert from_python == from_csharp, (
        f"CATALOG_TYPE_NAMES {sorted(from_python)} diverged from "
        f"CatalogSlotTypeNames in {CS_PATH.name}: {sorted(from_csharp)}"
    )


# --- AC 8: the snapshot producer over a fake HTTP seam ---------------------------------


def _fake_fetch(pages: dict[str, dict], calls: list[str]):
    def fake(url: str):
        calls.append(url)
        query = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)
        # The /Artists endpoint carries no IncludeItemTypes; key it as MusicArtist.
        kind = query.get("IncludeItemTypes", ["MusicArtist"])[0]
        if kind not in pages:
            raise AssertionError(f"unexpected item type requested: {kind}")
        return pages[kind]

    return fake


def test_snapshot_producer_authenticates_dedupes_and_sorts():
    calls: list[str] = []
    pages = {
        "Series": {
            "Items": [
                {"Name": "adolescencia"},
                {"Name": "Adolescencia"},
                {"Name": "Dark"},
                {"Name": "   "},
            ]
        },
        "MusicArtist": {"Items": [{"Name": "Zeta"}, {"Name": "Alpha"}]},
        "Movie": {"Items": []},
    }
    snapshot = producer_jellyfin.build_snapshot(
        "http://jf.test:8096",
        "sekret-123",
        fetch_json=_fake_fetch(pages, calls),
        item_types=("Series", "MusicArtist", "Movie"),
    )
    assert snapshot["schema"] == 1
    assert snapshot["producer"] == "jellyfin-names"
    table = snapshot["locales"]["*"]
    # Sorted (codepoint order), case-insensitively deduped, blanks dropped:
    # only the first spelling of the duplicate survives.
    assert [entry["value"] for entry in table["SeriesName"]] == ["Dark", "adolescencia"]
    assert [entry["value"] for entry in table["JellyfinArtist"]] == ["Alpha", "Zeta"]
    # Empty item types are omitted: an empty replacement would wipe the type.
    assert "MovieTitle" not in table
    assert len(calls) == 3
    for url in calls:
        assert "ApiKey=sekret-123" in url  # capital A and K, query parameter
        assert "api_key=" not in url
        assert "X-Emby-Token" not in url
    assert "/Artists?" in calls[1]
    assert "IncludeItemTypes=Series" in calls[0]


def test_snapshot_producer_is_deterministic_across_runs():
    pages = {
        "Series": {"Items": [{"Name": "Zed"}, {"Name": "Abbey"}]},
    }
    first = json.dumps(
        producer_jellyfin.build_snapshot(
            "http://jf.test", "k", fetch_json=_fake_fetch(pages, []), item_types=("Series",)
        ),
        sort_keys=True,
    )
    second = json.dumps(
        producer_jellyfin.build_snapshot(
            "http://jf.test", "k", fetch_json=_fake_fetch(pages, []), item_types=("Series",)
        ),
        sort_keys=True,
    )
    assert first == second


def test_snapshot_producer_output_passes_load_snapshot(tmp_path):
    pages = {"Series": {"Items": [{"Name": "Dark"}]}}
    snapshot = producer_jellyfin.build_snapshot(
        "http://jf.test", "k", fetch_json=_fake_fetch(pages, []), item_types=("Series",)
    )
    path = tmp_path / "roundtrip.json"
    path.write_text(json.dumps(snapshot), encoding="utf-8")
    assert load_snapshot(path) == snapshot


def test_snapshot_producer_rejects_unknown_item_type():
    with pytest.raises(ValueError, match="unknown Jellyfin item type"):
        producer_jellyfin.build_snapshot(
            "http://jf.test", "k", fetch_json=lambda url: {}, item_types=("Nonsense",)
        )


def test_resolve_connection_flags_win_over_env(monkeypatch):
    monkeypatch.setenv("JELLYFIN_URL", "http://env-url")
    monkeypatch.setenv("JELLYFIN_API_KEY", "env-key")
    assert producer_jellyfin.resolve_connection("http://flag", "flag-key") == ("http://flag", "flag-key")
    assert producer_jellyfin.resolve_connection(None, None) == ("http://env-url", "env-key")
    monkeypatch.delenv("JELLYFIN_URL")
    with pytest.raises(ValueError, match="JELLYFIN_URL"):
        producer_jellyfin.resolve_connection(None, None)


def test_redact_removes_key_and_query_assignment():
    text = "GET http://jf.test/Items?ApiKey=sekret-123&Recursive=true failed for sekret-123"
    out = producer_jellyfin.redact(text, "sekret-123")
    assert "sekret-123" not in out
    assert "apikey=<redacted>" in out


# --- AC 8/9: the CLI paths -------------------------------------------------------------


def test_cli_snapshot_never_prints_or_writes_the_key(tmp_path, monkeypatch, capsys):
    calls: list[str] = []
    pages = {"Series": {"Items": [{"Name": "Dark"}]}}
    monkeypatch.setattr(
        producer_jellyfin, "_http_get_json", _fake_fetch(pages, calls)
    )
    out_path = tmp_path / "snap.json"
    code = nlu_cli.main(
        [
            "snapshot",
            "--url",
            "http://jf.test:8096",
            "--api-key",
            "sekret-123",
            "--types",
            "Series",
            "--out",
            str(out_path),
        ]
    )
    assert code == 0
    captured = capsys.readouterr()
    assert "sekret-123" not in captured.out
    assert "sekret-123" not in captured.err
    written = out_path.read_text(encoding="utf-8")
    assert "sekret-123" not in written
    assert '"producer": "jellyfin-names"' in written
    assert calls and "ApiKey=sekret-123" in calls[0]


def test_cli_snapshot_redacts_failures(tmp_path, monkeypatch, capsys):
    def boom(url: str):
        raise RuntimeError(f"GET {url} exploded")

    monkeypatch.setattr(producer_jellyfin, "_http_get_json", boom)
    code = nlu_cli.main(
        ["snapshot", "--url", "http://jf.test", "--api-key", "sekret-123"]
    )
    assert code == 2
    captured = capsys.readouterr()
    assert "sekret-123" not in captured.out
    assert "sekret-123" not in captured.err
    assert "error" in captured.err


def test_cli_snapshot_missing_connection_is_a_clean_error(capsys, monkeypatch):
    monkeypatch.delenv("JELLYFIN_URL", raising=False)
    monkeypatch.delenv("JELLYFIN_API_KEY", raising=False)
    code = nlu_cli.main(["snapshot"])
    assert code == 2
    captured = capsys.readouterr()
    assert "JELLYFIN_URL" in captured.err
    assert "error" in captured.err


def test_cli_effective_byte_identical_across_runs(capsys):
    first_code = nlu_cli.main(["effective", "--locale", "es-ES"])
    first = capsys.readouterr().out
    second_code = nlu_cli.main(["effective", "--locale", "es-ES"])
    second = capsys.readouterr().out
    assert first_code == 0 and second_code == 0
    assert first == second
    assert first  # non-empty
    payload = json.loads(first)
    assert set(payload) == {"locale", "notes", "source", "types"}
    assert payload["locale"] == "es-ES"
    assert payload["source"] == "committed"
    assert payload["types"]["SeriesName"] == {
        "kind": "CATALOG",
        "entry_count": 10,
        "source": "committed",
    }


def test_cli_effective_no_timestamp_or_absolute_path(capsys):
    nlu_cli.main(["effective", "--locale", "es-ES"])
    out = capsys.readouterr().out
    assert str(REPO) not in out
    assert "/home/" not in out
    assert not re.search(r"\d{4}-\d{2}-\d{2}", out)
    assert not re.search(r"\d{2}:\d{2}:\d{2}", out)


def test_cli_effective_with_snapshot_reports_replacement(tmp_path, capsys):
    snapshot_path = tmp_path / "snap.json"
    snapshot_path.write_text(
        json.dumps(
            _snapshot(
                {"*": {"SeriesName": [{"value": "Adolescencia", "synonyms": []}]}}
            )
        ),
        encoding="utf-8",
    )
    code = nlu_cli.main(
        ["effective", "--locale", "es-ES", "--snapshot", str(snapshot_path)]
    )
    assert code == 0
    payload = json.loads(capsys.readouterr().out)
    assert payload["source"] == "snapshot"
    assert payload["types"]["SeriesName"]["source"] == "snapshot"
    assert payload["types"]["SeriesName"]["entry_count"] == 1


def test_cli_effective_errors_are_clean(capsys):
    assert nlu_cli.main(["effective", "--locale", "no-such-locale"]) == 2
    captured = capsys.readouterr()
    assert "error" in captured.err
    bad = nlu_cli.main(["effective", "--locale", "es-ES", "--snapshot", "/no/such/file.json"])
    assert bad == 2
    captured = capsys.readouterr()
    assert "error" in captured.err


# --- AC 10: the doc page ---------------------------------------------------------------


def test_docs_page_exists_with_contract_and_examples():
    doc = (REPO / "docs" / "nlu-static-analysis.md").read_text(encoding="utf-8")
    assert "nlu_static/cli.py snapshot" in doc
    assert "nlu_static/cli.py effective" in doc
    assert "jellyfin-names" in doc
    assert "schema" in doc
    assert "phonetic synonyms" in doc
    assert "140" in doc
    assert "50000" in doc
    assert "ApiKey=<key>" in doc
    assert "does not predict" in doc.lower() or "do not predict" in doc.lower()


def test_package_and_doc_prose_avoids_banned_punctuation():
    em_dash = chr(0x2014)  # built from the codepoint: the character never lands in this file
    package_files = sorted((SCRIPTS_DIR / "nlu_static").glob("*.py"))
    doc = REPO / "docs" / "nlu-static-analysis.md"
    for path in [*package_files, doc]:
        text = path.read_text(encoding="utf-8")
        assert em_dash not in text, path


# --- /simplify round (2026-11-24): the new seams ---------------------------------------


def test_build_effective_model_rejects_schemaless_snapshot_dict(tmp_path):
    """A raw dict passed to build_effective_model cannot bypass the schema check."""
    _write_model(tmp_path, "xx-XX", {"languageModel": _series_model({"SeriesName": ["Seed"]})})
    bogus = {"schema": 99, "locales": {"xx-XX": {"SeriesName": [{"value": "x", "synonyms": []}]}}}
    with pytest.raises(ValueError) as excinfo:
        build_effective_model("xx-XX", snapshot=bogus, models_dir=tmp_path)
    assert "schema" in str(excinfo.value)


def test_skip_line_is_none_for_tokenizable_locales_and_mandated_otherwise():
    assert skip_line("it-IT") is None
    assert skip_line("es-ES") is None
    ja = skip_line("ja-JP")
    assert ja == "SKIP ja-JP: no whitespace tokenization"


def test_producer_raises_on_unexpected_payload_shape():
    def fake_fetch(_url):
        return {"not": "the items envelope"}

    with pytest.raises(ValueError) as excinfo:
        producer_jellyfin.build_snapshot(
            "http://server", "key", fetch_json=fake_fetch, item_types=("Series",)
        )
    assert "unexpected payload" in str(excinfo.value)

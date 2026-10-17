"""JF-855: the pytest harness for the generator's `mirror` directive.

The es-MX / es-US PlayEpisodeIntent families are DECLARED MIRRORS of es-ES:
the generator sources their samples from templates/es-ES.yaml in full (the
JF-844 mirror round of 2026-10-17 deleted the last exclude lists; the
directive still supports `exclude` for future divergences, asserted on
synthetic templates below), so the JF-844.1 mirroring lint and its whitelist
are retired and every drift shape it caught after the fact is now a
generation error. The harness asserts each error shape on synthetic
templates (the generator's real template loader is pointed at a temp dir),
the real tree's golden master (regenerating es-MX / es-US reproduces the
committed JSONs byte-identically), and the retirement itself (the lint and
its tables no longer exist in the validator).
"""
import json
import shutil
import sys
from pathlib import Path

import pytest
import yaml

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import generate_interaction_model as generator  # noqa: E402

REPO = Path(__file__).resolve().parents[2]

SHARED = "reproduce {series_name} temporada {season_number} episodio {episode_number}"
CONNECTORS = [
    "que reproduzca {series_name} temporada {season_number} episodio {episode_number}",
    "que reproduzca la temporada {season_number} episodio {episode_number} de {series_name}",
    "que reproduzca el episodio {episode_number} de {series_name}",
]


def _ref_template(samples: list[str], intent_name: str = "PlayEpisodeIntent") -> dict:
    return {
        "invocationName": "mi colección",
        "intents": [{"name": intent_name, "samples": list(samples)}],
    }


def _mirror_template(exclude: list, ref: str = "es-ES",
                     intent_name: str = "PlayEpisodeIntent") -> dict:
    return {
        "invocationName": "mi colección",
        "intents": [
            {
                "name": intent_name,
                "slots": [
                    {"name": "series_name", "type": "SeriesName"},
                    {"name": "season_number", "type": "AMAZON.NUMBER"},
                    {"name": "episode_number", "type": "AMAZON.NUMBER"},
                ],
                "mirror": {"locale": ref, "exclude": exclude},
            }
        ],
    }


@pytest.fixture
def templates_dir(tmp_path, monkeypatch):
    """Point the generator's MODELS_DIR at a temp templates tree."""
    monkeypatch.setattr(generator, "MODELS_DIR", tmp_path)
    yield tmp_path


def _write_ref(dir_, samples: list[str]):
    (dir_ / "templates").mkdir(exist_ok=True)
    with open(dir_ / "templates" / "es-ES.yaml", "w") as f:
        yaml.safe_dump(_ref_template(samples), f)


def _build_sibling(dir_, exclude, ref="es-ES"):
    return generator.build_model(_mirror_template(exclude, ref), locale="es-MX")


def test_mirror_inherits_reference_samples_in_order(templates_dir):
    _write_ref(templates_dir, [SHARED, *CONNECTORS])
    model = _build_sibling(templates_dir, CONNECTORS[:1])
    intent = model["languageModel"]["intents"][0]
    assert intent["samples"] == [SHARED, *CONNECTORS[1:]]


def test_mirror_without_exclude_takes_all_rows(templates_dir):
    _write_ref(templates_dir, [SHARED, *CONNECTORS])
    model = _build_sibling(templates_dir, [])
    assert model["languageModel"]["intents"][0]["samples"] == [SHARED, *CONNECTORS]


def test_stale_exclude_row_is_a_generation_error(templates_dir):
    _write_ref(templates_dir, [SHARED])
    stale = CONNECTORS[0]
    with pytest.raises(ValueError, match="no longer carries"):
        _build_sibling(templates_dir, [stale])


def test_missing_reference_template_is_a_generation_error(templates_dir):
    with pytest.raises(ValueError, match="not found"):
        _build_sibling(templates_dir, [])


def test_missing_reference_intent_is_a_generation_error(templates_dir):
    # The reference template exists but carries no PlayEpisodeIntent.
    (templates_dir / "templates").mkdir(exist_ok=True)
    with open(templates_dir / "templates" / "es-ES.yaml", "w") as f:
        yaml.safe_dump(_ref_template([SHARED], intent_name="OtherIntent"), f)
    with pytest.raises(ValueError, match="no .*PlayEpisodeIntent.* intent"):
        _build_sibling(templates_dir, [])


def test_mirror_chain_resolves_transitively(templates_dir):
    _write_ref(templates_dir, [SHARED, *CONNECTORS])
    (templates_dir / "templates" / "es-US.yaml").write_text(
        yaml.safe_dump(_mirror_template([], "es-ES"))
    )
    model = generator.build_model(
        _mirror_template([CONNECTORS[0]], "es-US"), locale="es-MX"
    )
    assert model["languageModel"]["intents"][0]["samples"] == [SHARED, *CONNECTORS[1:]]


def test_mirror_cycle_is_a_generation_error(templates_dir):
    for locale in ("es-ES", "es-MX"):
        (templates_dir / "templates").mkdir(exist_ok=True)
        (templates_dir / "templates" / f"{locale}.yaml").write_text(
            yaml.safe_dump(_mirror_template([], "es-US"))
        )
    with pytest.raises(ValueError, match="cycle"):
        generator.build_model(
            _mirror_template([], "es-ES"), locale="es-US"
        )


def test_self_mirror_is_a_generation_error(templates_dir):
    with pytest.raises(ValueError, match="cycle"):
        _build_sibling(templates_dir, [], ref="es-MX")


def test_mirror_plus_samples_is_a_generation_error(templates_dir):
    _write_ref(templates_dir, [SHARED])
    cfg = _mirror_template([], ref="es-ES")
    cfg["intents"][0]["samples"] = ["otra frase"]
    with pytest.raises(ValueError, match="exactly one sample source"):
        generator.build_model(cfg, locale="es-MX")


def test_unknown_mirror_key_is_a_generation_error(templates_dir):
    _write_ref(templates_dir, [SHARED])
    cfg = _mirror_template([])
    cfg["intents"][0]["mirror"]["whitelist"] = [SHARED]
    with pytest.raises(ValueError, match="unknown key"):
        generator.build_model(cfg, locale="es-MX")


def test_empty_mirror_result_is_a_generation_error(templates_dir):
    _write_ref(templates_dir, [SHARED])
    with pytest.raises(ValueError, match="zero"):
        _build_sibling(templates_dir, [SHARED])


def test_build_without_locale_raises_on_mirror_template(templates_dir):
    _write_ref(templates_dir, [SHARED])
    with pytest.raises(ValueError, match="building locale"):
        generator.build_model(_mirror_template([]))


def test_committed_es_siblings_regenerate_byte_identically():
    """The golden master: the committed es-MX / es-US JSONs are what the
    mirror directive produces, which pins the mirrored family byte-for-byte
    (a drifted row makes this fail with a generation error or a byte diff)."""
    for locale in ("es-MX", "es-US"):
        template = yaml.safe_load(
            (REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel"
             / "templates" / f"{locale}.yaml").read_text()
        )
        model = generator.build_model(template, locale=locale)
        committed = (
            REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel"
            / f"model_{locale}.json"
        ).read_text()
        assert generator.serialize_model(model) == committed


def test_mirror_sample_order_matches_es_es_not_sibling_transcription():
    """The mirror's samples must equal es-ES's FULL PlayEpisode sample list,
    in es-ES's order (the retired lint's subset contract, by construction).
    JF-844 mirror round (2026-10-17): the connector-row exclude lists were
    deleted after the es-ES connector probe went green, so the sibling now
    carries every es-ES row, connectors included."""
    lm = json.loads(
        (REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel"
         / "model_es-MX.json").read_text()
    )
    ref = json.loads(
        (REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel"
         / "model_es-ES.json").read_text()
    )
    get = lambda d: next(
        i["samples"] for i in d["languageModel"]["intents"]
        if i["name"] == "PlayEpisodeIntent"
    )
    assert get(lm) == get(ref)


def test_es_trio_lint_is_retired():
    import validate_interaction_models as validator

    assert not hasattr(validator, "lint_es_trio_play_episode_mirroring")
    assert not hasattr(validator, "ES_TRIO_PLAY_EPISODE_DIVERGENCES")
    assert not hasattr(validator, "ES_TRIO_CONNECTOR_TWINS")
    assert not hasattr(validator, "ES_TRIO_REFERENCE_LOCALE")


def test_mirrored_row_with_undeclared_slot_is_a_generation_error(templates_dir):
    # The reviewer finding applied: mirrored samples run the same {ref} guard
    # as generate_samples; a reference row using a slot the sibling does not
    # declare is an error, not a silent broken sample.
    _write_ref(templates_dir, ["mira {series_name} temporada {season_number}"])
    cfg = _mirror_template([])
    cfg["intents"][0]["slots"] = [{"name": "series_name", "type": "SeriesName"}]
    with pytest.raises(ValueError, match="does not declare as a slot"):
        generator.build_model(cfg, locale="es-MX")

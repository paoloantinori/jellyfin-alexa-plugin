"""JF-905: the pytest harness for the LoopAllOn queue-noun check (#12).

Every LoopAllOnIntent sample must name the queue: a sample that drops the noun
regresses to the bare-loop class JF-902 eliminated (a bare loop sample is
repeat-ONE, so the queue utterance routed there silently repeats the wrong
thing). JF-904 gave every locale queue-named carriers and the fixtures pin
them; this check makes the invariant self-enforcing in the validator, so the
regression surfaces at validation instead of waiting for a fixture or a live
NLU steal.

The check lives inside validate_single_model (warning-level, the #10 bare
album carrier precedent), so the harness feeds synthetic languageModels and
asserts the warning list; no repo mutation. QUEUE_CARRIER_NOUNS is sourced
from each locale's ListQueue/ClearQueue vocabulary exactly as the JF-904
carriers were.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))

from test_elicit_checker import _models  # noqa: E402
from validate_interaction_models import (  # noqa: E402
    QUEUE_CARRIER_NOUNS,
    validate_single_model,
)


def _lm(samples: list[str]) -> dict:
    return {
        "invocationName": "test",
        "intents": [
            {"name": "LoopAllOnIntent", "samples": list(samples), "slots": []},
        ],
    }


def _jf905_warnings(locale: str, samples: list[str]) -> list[str]:
    _errors, warnings = validate_single_model(locale, _lm(samples))
    return [w for w in warnings if "JF-905" in w]


# --- Dirty shapes: one bare-loop regression per language family -------------


def test_bare_repeat_warns_en():
    warnings = _jf905_warnings("en-US", ["repeat", "loop"])
    assert len(warnings) == 2, warnings
    assert "'repeat'" in warnings[0], warnings


def test_bare_loop_warns_every_language_family():
    # One realistic bare-loop regression per QUEUE_CARRIER_NOUNS key: if a
    # family goes missing from the table, its row here fails (the check would
    # silently skip it, the same unlisted-locale trade #10 makes).
    regressions = {
        "en-US": "repeat the whole thing",
        "de-DE": "Wiederhole alles",
        "es-ES": "Repite todo",
        "fr-FR": "Répète tout",
        "it-IT": "Ripeti tutto",
        "ja-JP": "全部リピートして",
        "nl-NL": "Herhaal alles",
        "pt-BR": "Repetir tudo",
        "hi-IN": "सब दोहराओ",
        "ar-SA": "كرر كل شيء",
    }
    for locale, sample in regressions.items():
        warnings = _jf905_warnings(locale, [sample])
        assert len(warnings) == 1, (locale, warnings)
        assert sample in warnings[0], (locale, warnings[0])


# --- Clean shapes: the real JF-904 carriers and their orthographic variants --


def test_jf904_carrier_shapes_stay_clean():
    # One real carrier per family, taken verbatim from the committed models.
    carriers = {
        "en-US": "repeat the queue",
        "de-DE": "Wiederhole die ganze Warteschlange",
        "es-ES": "Repite toda la cola",
        "fr-FR": "Mets la file d'attente en boucle",
        "it-IT": "Ripeti tutta la coda",
        "ja-JP": "キュー全体をリピートして",
        "nl-NL": "zet de wachtrij op repeat",
        "pt-BR": "colocar a fila em loop",
        "hi-IN": "पूरी कतार दोहराओ",
        "ar-SA": "كرر قائمة الانتظار بالكامل",
    }
    for locale, sample in carriers.items():
        warnings = _jf905_warnings(locale, [sample])
        assert warnings == [], (locale, warnings)


def test_matching_is_case_insensitive():
    warnings = _jf905_warnings("en-US", ["REPEAT THE QUEUE"])
    assert warnings == [], warnings


def test_fr_typographic_apostrophe_stays_clean():
    # The fr stem is 'attente', not the full "file d'attente": a typographic
    # apostrophe (U+2019) inside an otherwise correct carrier must not warn.
    warnings = _jf905_warnings("fr-FR", ["Répète la file d’attente"])
    assert warnings == [], warnings


def test_ar_hamza_variant_and_article_elision_stay_clean():
    # The ar stem is 'نتظار', not the full "قائمة الانتظار": the leading alif
    # is dropped (the ALBUM_CARRIER_NOUNS ar stem rationale) so the hamza
    # variant إنتظار and the article-elided form انتظار both match.
    warnings = _jf905_warnings("ar-SA", ["كرر قائمة الإنتظار", "كرر قائمة انتظار"])
    assert warnings == [], warnings


def test_placeholder_strip_cannot_fake_a_noun():
    # Placeholders are stripped BEFORE the noun check (#10 parity), so a slot
    # whose future value would carry the noun cannot defeat detection.
    warnings = _jf905_warnings("en-US", ["repeat {media}"])
    assert len(warnings) == 1, warnings


def test_placeholder_carrier_with_noun_stays_clean():
    warnings = _jf905_warnings("en-US", ["repeat the {media} queue"])
    assert warnings == [], warnings


def test_absent_intent_is_out_of_scope():
    # A locale without LoopAllOnIntent has no carrier invariant to enforce;
    # cross-locale intent checks own that shape. The model carries an unrelated
    # intent because an empty intents array short-circuits validate_single_model
    # before check #12 is ever reached.
    lm = {
        "invocationName": "test",
        "intents": [{"name": "PlaySongIntent", "samples": ["play a song"], "slots": []}],
    }
    _errors, warnings = validate_single_model("en-US", lm)
    assert not any("JF-905" in w for w in warnings), warnings


def test_untabled_locale_declaring_the_intent_warns():
    # A language prefix with no QUEUE_CARRIER_NOUNS entry must not become a
    # silently unenforced locale: the missing-entry warning fires (and the
    # real-tree test below turns it into a hard failure for committed models).
    warnings = _jf905_warnings("zz-ZZ", ["repeat"])
    assert len(warnings) == 1, warnings
    assert "no QUEUE_CARRIER_NOUNS entry" in warnings[0], warnings


def test_table_covers_every_declaring_locale():
    # Direct coverage pin over the committed corpus: every model declaring
    # LoopAllOnIntent must have a QUEUE_CARRIER_NOUNS entry for its language
    # prefix (the table-coverage half of the missing-entry guard).
    models = _models()
    for locale, lm in models.items():
        declares = any(i.get("name") == "LoopAllOnIntent" for i in lm.get("intents", []))
        if declares:
            assert locale.split("-")[0] in QUEUE_CARRIER_NOUNS, locale


# --- The real tree: all 17 committed models must stay clean ----------------


def test_all_committed_models_have_queue_named_carriers():
    models = _models()
    assert len(models) == 17, len(models)
    for locale, lm in models.items():
        _errors, warnings = validate_single_model(locale, lm)
        assert not any("JF-905" in w for w in warnings), (locale, warnings)

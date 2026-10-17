"""Unit tests for the fr one-shot fallback composition and the typed
failure detail (JF-900).

Pure decision seams, no SMAPI, no network, no subprocess. Run standalone:

    tests/integration/.venv/bin/python -m pytest test_fallback_one_shot_composition.py -v
"""

from smapi_client import (
    SmapiClient,
    SmapiError,
    SmapiSelectionLostError,
)
from test_e2e import smapi_failure_detail


# ---------------------------------------------------------------------------
# compose_fallback_one_shot: the fr imperative -> infinitive rewrite
# ---------------------------------------------------------------------------


def test_map_covers_the_fixture_imperatives():
    # The bare-imperative shapes the fr smoke fixtures carry (JF-511);
    # these are exactly the fallback compositions that went out
    # ungrammatical ("demande à mon serveur de lis ...") before JF-900.
    assert (
        SmapiClient.compose_fallback_one_shot("lis mes favoris", "fr-FR")
        == "lire mes favoris"
    )
    assert (
        SmapiClient.compose_fallback_one_shot(
            "lis les chansons de pink floyd", "fr-FR"
        )
        == "lire les chansons de pink floyd"
    )
    assert (
        SmapiClient.compose_fallback_one_shot("joue un morceau aléatoire", "fr-FR")
        == "jouer un morceau aléatoire"
    )
    assert (
        SmapiClient.compose_fallback_one_shot("mets le film barbie", "fr-FR")
        == "mettre le film barbie"
    )


def test_map_covers_fr_ca_and_remaining_verbs():
    assert (
        SmapiClient.compose_fallback_one_shot("mets le film barbie", "fr-CA")
        == "mettre le film barbie"
    )
    assert (
        SmapiClient.compose_fallback_one_shot("ferme la porte", "fr-FR")
        == "fermer la porte"
    )
    assert (
        SmapiClient.compose_fallback_one_shot("ouvre le film barbie", "fr-FR")
        == "ouvrir le film barbie"
    )
    assert (
        SmapiClient.compose_fallback_one_shot("arrete la musique", "fr-FR")
        == "arrêter la musique"
    )


def test_met_variant_maps_to_mettre():
    # The trivially derivable imperative variant sanctioned by the spec.
    assert (
        SmapiClient.compose_fallback_one_shot("met le film barbie", "fr-FR")
        == "mettre le film barbie"
    )


def test_unknown_leading_verb_passes_through():
    # Safe default: "je veux regarder barbie" leads with a word the map
    # does not know; the fallback composes it unchanged rather than
    # guessing a rewrite.
    assert (
        SmapiClient.compose_fallback_one_shot("je veux regarder barbie", "fr-FR")
        == "je veux regarder barbie"
    )


def test_already_infinitive_utterance_passes_through():
    # The map carries the imperative keys only, so a row already led by
    # the infinitive ("mets/mettre dedupe") is never touched.
    assert (
        SmapiClient.compose_fallback_one_shot("mettre le film barbie", "fr-FR")
        == "mettre le film barbie"
    )


def test_full_chain_composition_passes_byte_identical():
    # A row carrying its own full one-shot composition (prefix included)
    # must never be rewritten: the head "demande" is not an imperative key.
    utterance = "demande à mon serveur de lire les chansons de pink floyd"
    assert SmapiClient.compose_fallback_one_shot(utterance, "fr-FR") == utterance


def test_open_utterance_passes_byte_identical():
    # The smoke open verb is its own authored row ("lance" fr-CA / "ouvre"
    # fr-FR): "ouvre" IS a key, but only the fallback COMMAND composition
    # calls the composer, so the pin here is the composer itself staying
    # honest about "lance" (no mapping); the call-site scoping is what
    # keeps the open row untouched.
    assert (
        SmapiClient.compose_fallback_one_shot("lance mon serveur", "fr-CA")
        == "lance mon serveur"
    )


def test_non_fr_locales_are_byte_identical():
    # The composition is fr-only even where the same leading word occurs.
    assert (
        SmapiClient.compose_fallback_one_shot("lis mes favoris", "it-IT")
        == "lis mes favoris"
    )
    assert (
        SmapiClient.compose_fallback_one_shot("mets le film barbie", "en-US")
        == "mets le film barbie"
    )


def test_single_word_utterance_maps():
    assert SmapiClient.compose_fallback_one_shot("lis", "fr-FR") == "lire"


def test_head_match_is_word_boundaried():
    # Only a whole leading word maps: "lis" inside a longer first token
    # ("lisbonne" as a spoken title) must not trigger the rewrite.
    assert (
        SmapiClient.compose_fallback_one_shot("lisbonne histoire", "fr-FR")
        == "lisbonne histoire"
    )


# ---------------------------------------------------------------------------
# smapi_failure_detail: the typed class named in the step-2 failure text
# ---------------------------------------------------------------------------


def test_failure_detail_names_the_selection_lost_class():
    # The shared "did not resolve to any intent" marker reads as an NLU
    # no-match in the raw cause text; the typed class name is the honest
    # discriminator, because the FAILED simulation's consideredIntents are
    # not retrievable after the client raises.
    detail = smapi_failure_detail(
        SmapiSelectionLostError(
            "Simulation failed after 12.3s for 'lis mes favoris' (fr-FR): "
            "This utterance did not resolve to any intent in your skill"
        )
    )
    assert detail.startswith("SmapiSelectionLostError: ")
    assert "did not resolve to any intent" in detail


def test_failure_detail_names_the_plain_class():
    detail = smapi_failure_detail(SmapiError("Simulation timed out after 60s"))
    assert detail.startswith("SmapiError: ")


def test_failure_detail_distinguishes_the_classes():
    # The property the step-2 report needs: identical raw cause text, two
    # different named classes, so a selection loss is no longer
    # indistinguishable from a genuine no-match in the failure output.
    cause = "did not resolve to any intent"
    lost = smapi_failure_detail(SmapiSelectionLostError(cause))
    plain = smapi_failure_detail(SmapiError(cause))
    assert lost.split(": ", 1)[0] != plain.split(": ", 1)[0]


def test_failure_detail_truncates_the_message():
    detail = smapi_failure_detail(SmapiError("x" * 500))
    message_part = detail.split(": ", 1)[1]
    assert len(message_part) == 120

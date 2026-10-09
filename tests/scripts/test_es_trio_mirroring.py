"""JF-844.1: the pytest harness for the es trio PlayEpisode mirroring lint.

The es locales are verbatim-transcription templates with no shared-include
mechanism (the JF-316 golden-master decision), so the PlayEpisodeIntent rows
are hand-mirrored across es-ES / es-MX / es-US and nothing else guarded that
mirroring. The lint makes it data: a sibling row es-ES lacks fires (the
mirroring broke), an es-ES-only surplus row outside the whitelist fires (the
single-locale drift class commit 22fed1ab performed deliberately for the
connector rows), and a whitelisted row that stopped being a divergence fires
(whitelist rot). The harness feeds synthetic trio models and asserts each
finding shape; the committed tree must stay quiet, which also pins the
whitelist rows byte-for-byte against the real models (a typo'd row makes the
clean-tree test fail with a stale-entry finding).
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))

from test_elicit_checker import _models  # noqa: E402
from validate_interaction_models import (  # noqa: E402
    ES_TRIO_PLAY_EPISODE_DIVERGENCES,
    lint_es_trio_play_episode_mirroring,
)

# One shared row and one real whitelisted connector row, verbatim from the
# committed models: the synthetic trio exercises the ACTUAL whitelist data.
SHARED = "reproduce {series_name} temporada {season_number} episodio {episode_number}"
CONNECTORS = sorted(ES_TRIO_PLAY_EPISODE_DIVERGENCES["es-MX"])


def _lm(samples: list[str]) -> dict:
    return {"intents": [{"name": "PlayEpisodeIntent", "samples": list(samples)}]}


def _trio(es: list[str], mx: list[str], us: list[str]) -> dict[str, dict]:
    return {"es-ES": _lm(es), "es-MX": _lm(mx), "es-US": _lm(us)}


def test_clean_synthetic_trio_is_quiet():
    warnings = lint_es_trio_play_episode_mirroring(
        _trio([SHARED] + CONNECTORS, [SHARED], [SHARED])
    )
    assert warnings == [], warnings


def test_committed_tree_is_quiet():
    # The real models are the trio reference implementation: quiet here means
    # the whitelist equals the actual per-sibling surplus exactly.
    warnings = lint_es_trio_play_episode_mirroring(_models())
    assert warnings == [], warnings


def test_shared_row_removed_from_es_es_fires_both_siblings():
    # The JF-844.1 acceptance shape: a deliberate single-locale removal from
    # es-ES leaves both siblings carrying a row es-ES lacks.
    warnings = lint_es_trio_play_episode_mirroring(
        _trio(CONNECTORS, [SHARED], [SHARED])
    )
    assert len(warnings) == 2, warnings
    assert all(SHARED in w and "not in es-ES" in w for w in warnings), warnings
    assert {"[es-MX]", "[es-US]"} == {w.strip().split("]")[0] + "]" for w in warnings}


def test_sibling_only_row_fires_that_sibling():
    warnings = lint_es_trio_play_episode_mirroring(
        _trio([SHARED] + CONNECTORS, [SHARED, "pon la temporada {season_number} episodio {episode_number}"], [SHARED])
    )
    assert len(warnings) == 1, warnings
    assert "[es-MX]" in warnings[0] and "not in es-ES" in warnings[0], warnings


def test_unlisted_es_es_only_row_fires_both_siblings():
    extra = "que mira {series_name} temporada {season_number} episodio {episode_number}"
    warnings = lint_es_trio_play_episode_mirroring(
        _trio([SHARED] + CONNECTORS + [extra], [SHARED], [SHARED])
    )
    assert len(warnings) == 2, warnings
    assert all(extra in w and "not in the divergence whitelist" in w for w in warnings), warnings


def test_mirrored_whitelisted_row_fires_stale_entry():
    # Connector row inherited by es-MX only: the whitelist no longer matches
    # that sibling's surplus and must be updated in the same change.
    warnings = lint_es_trio_play_episode_mirroring(
        _trio([SHARED] + CONNECTORS, [SHARED, CONNECTORS[0]], [SHARED])
    )
    assert len(warnings) == 1, warnings
    assert CONNECTORS[0] in warnings[0] and "[es-MX]" in warnings[0], warnings
    assert "no longer es-ES-only" in warnings[0], warnings


def test_findings_carry_the_task_marker():
    warnings = lint_es_trio_play_episode_mirroring(_trio(CONNECTORS, [SHARED], [SHARED]))
    assert warnings and all("JF-844.1" in w for w in warnings), warnings


def test_coverage_finding_still_fires_when_parity_skips(capsys):
    # JF-852/F2: a new es locale added in the same window as a missing trio
    # member must not escape silently; the SKIP print stays AND the coverage
    # finding prints at the phase level.
    trio = _trio([SHARED] + CONNECTORS, [SHARED], [SHARED])
    trio["es-AR"] = _lm([SHARED])
    del trio["es-US"]
    assert lint_es_trio_play_episode_mirroring(trio) is None
    out = capsys.readouterr().out
    assert "  SKIP: [es-US] model absent, es trio mirroring lint not run\n" in out
    assert (
        "  WARN (coverage, skipped parity): [es-AR] es locale present but no "
        "ES_TRIO_PLAY_EPISODE_DIVERGENCES row; the PlayEpisode mirroring lint "
        "does not cover it (add one in the same change) (JF-844.1)\n" in out
    )


def test_missing_sibling_locale_skips_not_all_clear(capsys):
    pair = {"es-ES": _lm([SHARED] + CONNECTORS), "es-MX": _lm([SHARED])}
    assert lint_es_trio_play_episode_mirroring(pair) is None
    # JF-612: the SKIP print is the load-bearing honest signal; pin it exactly.
    assert "  SKIP: [es-US] model absent, es trio mirroring lint not run\n" in capsys.readouterr().out


def test_missing_reference_locale_skips_not_all_clear(capsys):
    pair = {"es-MX": _lm([SHARED]), "es-US": _lm([SHARED])}
    assert lint_es_trio_play_episode_mirroring(pair) is None
    assert "  SKIP: [es-ES] model absent, es trio mirroring lint not run\n" in capsys.readouterr().out


def test_missing_intent_skips_not_all_clear(capsys):
    trio = _trio([SHARED] + CONNECTORS, [SHARED], [SHARED])
    trio["es-US"] = {"intents": []}
    trio["es-AR"] = _lm([SHARED])
    assert lint_es_trio_play_episode_mirroring(trio) is None
    out = capsys.readouterr().out
    assert (
        "  SKIP: [es-US] has no PlayEpisodeIntent, es trio mirroring lint not run\n"
        in out
    )
    # The intent-None skip arm must ALSO emit the coverage finding (JF-852).
    assert "  WARN (coverage, skipped parity): [es-AR]" in out


def test_unlisted_es_locale_fires_coverage_finding():
    # JF-824 convention: a new es locale must join the sibling table in the
    # same change, or it sits silently outside the lint. Also guards the
    # (JF-844.1) marker the coverage arm owes its convention (JF-852).
    trio = _trio([SHARED] + CONNECTORS, [SHARED], [SHARED])
    trio["es-AR"] = _lm([SHARED])
    warnings = lint_es_trio_play_episode_mirroring(trio)
    assert len(warnings) == 1, warnings
    assert "[es-AR]" in warnings[0] and "no ES_TRIO_PLAY_EPISODE_DIVERGENCES" in warnings[0], warnings
    assert "JF-844.1" in warnings[0], warnings


def test_non_es_locale_outside_lint_stays_quiet():
    # Only the es family is the trio contract; other locales joining all_models
    # are none of this lint's business.
    quartet = _trio([SHARED] + CONNECTORS, [SHARED], [SHARED])
    quartet["pt-BR"] = _lm(["toca {series_name} temporada {season_number}"])
    assert lint_es_trio_play_episode_mirroring(quartet) == []

"""JF-613: the pytest harness for the Phase 8 elicit checker.

The wrong-table/shape/comment battery that used to exist only as manual probe
files now runs in the suite: the checker function takes injected sources, so
every failure mode is fixture-driven, no repo mutation.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))

from validate_interaction_models import check_elicit_dialog_registration  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
SRC_ROOT = REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa"

GOOD_HANDLER = '''
public class PlayPlaylistIntentHandler : BaseHandler
{
    public override Task<SkillResponse> HandleAsync(...)
    {
        return BuildDialogElicitResponse(
            "DidNotCatchPlaylistName", locale, "playlist",
            IntentNames.PlayPlaylist, Util.ElicitSlots.For(IntentNames.PlayPlaylist));
    }
}
'''


import functools


@functools.lru_cache(maxsize=1)
def _sources_base() -> dict[str, str]:
    """The real tree, read once per run (immutable content; the checker never
    mutates the passed dict - verified)."""
    srcs = {}
    for f in (SRC_ROOT).rglob("*.cs"):
        if "bin" in f.parts or "obj" in f.parts:
            continue
        srcs[str(f)] = f.read_text(encoding="utf-8")
    return srcs


def _sources(table: str | None = None, handler: str | None = None) -> dict[str, str]:
    """The real tree; None keeps the real file for that slot."""
    srcs = dict(_sources_base())
    if table is not None:
        srcs[str(SRC_ROOT / "Util" / "ElicitSlots.cs")] = table
    if handler is not None:
        srcs[str(SRC_ROOT / "Handler" / "Intent" / "PlayPlaylistIntentHandler.cs")] = handler
    return srcs


@functools.lru_cache(maxsize=1)
def _models() -> dict[str, dict]:
    import json
    models = {}
    for p in sorted((REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel").glob("model_*.json")):
        d = json.loads(p.read_text(encoding="utf-8"))
        models[p.name[6:-5]] = d.get("interactionModel", d)["languageModel"]
    return models


def test_default_sources_path_passes():
    """sources=None must exercise the checker's own repo walk (the CLI path);
    a default-glob regression would otherwise leave every other test green."""
    errors, _ = check_elicit_dialog_registration()
    assert errors == [], errors


def test_clean_tree_passes():
    errors, _ = check_elicit_dialog_registration(_sources())
    assert errors == [], errors


def _real_table() -> str:
    return (SRC_ROOT / "Util" / "ElicitSlots.cs").read_text(encoding="utf-8")


def test_wrong_table_entry_fails():
    bad_table = _real_table().replace(
        "[IntentNames.PlayPlaylist] = new[] { IntentNames.Slots.Playlist },",
        '[IntentNames.PlayPlaylist] = new[] { IntentNames.Slots.Playlist, "bogus_slot" },',
    )
    errors, _ = check_elicit_dialog_registration(_sources(table=bad_table))
    assert any("ElicitSlots table declares" in e for e in errors), errors


def test_missing_table_entry_fails():
    bad_table = "public static class ElicitSlots { }"
    errors, _ = check_elicit_dialog_registration(_sources(table=bad_table))
    assert any("no ElicitSlots table entry" in e or "zero entries" in e for e in errors), errors


def test_hand_built_array_fails():
    bad_handler = GOOD_HANDLER.replace(
        "Util.ElicitSlots.For(IntentNames.PlayPlaylist)",
        'new[] { "playlist" }',
    )
    errors, _ = check_elicit_dialog_registration(_sources(handler=bad_handler))
    assert any("does not pass ElicitSlots.For" in e for e in errors), errors


def test_comment_poisoned_table_ignored():
    poisoned = _real_table().replace(
        "[IntentNames.PlayPlaylist] =",
        '// [IntentNames.Bogus] = new[] { "x" },\n        [IntentNames.PlayPlaylist] =',
    )
    assert "// [IntentNames.Bogus]" in poisoned
    errors, _ = check_elicit_dialog_registration(_sources(table=poisoned))
    assert not any("Bogus" in e for e in errors), errors

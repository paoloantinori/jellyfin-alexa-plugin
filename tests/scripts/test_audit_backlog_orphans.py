"""JF-854: the pytest harness for the backlog orphan-census audit.

scripts/audit_backlog_orphans.py is the repo's only raw-tail detector: the
backlog MCP silently drops content that sits outside its managed marker
regions when it rewrites a task file (seven-occurrence gotcha). The harness
pins the extractor's managed-region rules on tmp_path fixtures (frontmatter,
BEGIN/END regions, nesting, unclosed-BEGIN-to-EOF, stray END, scaffold
headings, whitespace-only runs) and the --check exit contract, then walks the
REAL backlog/tasks tree: the three JF-853-parked files must be the only raw
ones until JF-853 normalizes their marker pathologies and folds them.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))

import audit_backlog_orphans as audit  # noqa: E402

# The script owns the layout constant; the guard must pin the same tree CI
# audits, not a recomputed copy of it.
TASKS_DIR = audit.DEFAULT_TASKS_DIR

# JF-853 owns these three: each carries a marker pathology whose fold was
# PROVEN destructive before normalization (jf-637 0->5 lines lost, jf-643
# 0->31, jf-781 3->46 on the sweep's roundtrip battery), so their orphans
# stay raw BY EVIDENCE until JF-853 normalizes the markers and folds them.
# When a member leaves backlog/tasks/, delete its row in the same change;
# once all three are folded the parked set below is empty and this guard is
# green with no further edits.
KNOWN_RAW_PARKED = {
    "jf-637 - JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md",
    "jf-643 - JF-643-katakana-query-values-never-match-Latin-library-names-script-gap-in-fuzzy-phonetic-search-naturalized-ja-JP-artist-and-genre-requests-all-end-not-found.md",
    "jf-781 - the-SearchMedia-fuzzy-pass-kana-gate-keeps-refuse-and-stop-non-Audio-kinds-have-no-walk-nor-retry-recovery.md",
}


CLEAN_TASK = """\
---
id: JF-900
title: clean task
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A body line the MCP owns.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] item
<!-- DOD:END -->
"""

# Appends a raw tail at L16: the classic shape the MCP drops on rewrite.
RAW_TAIL_TASK = CLEAN_TASK + "CLOSED 2026-10-05 by the orchestrator: raw tail.\n"

# L7 is raw junk; L9 opens a region that never closes, so L9 to EOF is
# managed (the conservative to-EOF rule) and the notes content never flags.
UNCLOSED_TASK = """\
---
id: JF-901
---

## Description

raw junk before the unclosed region

<!-- SECTION:NOTES:ORCHESTRATOR-SIMPLIFY:BEGIN -->
notes the MCP owns to EOF
even though the region never closes
"""

# L7 is a stray END at depth 0: scaffolding, not content; L8 is still raw.
STRAY_END_TASK = """\
---
id: JF-902
---

## Description

<!-- SECTION:DESCRIPTION:END -->
content after a stray END is still raw
"""

# L12 is the duplicated-section pathology: exact-match scaffold exclusion
# does not cover it, so it flags together with its body line.
DATED_DUPLICATE_TASK = """\
---
id: JF-903
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
body
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes (2026-09-27)
a duplicated section is the raw-tail pathology
"""

# Regions nest: the inner pop covers L7-L9, the outer pop covers L5-L11, so
# only the L12 tail after the outer close is raw.
NESTED_TASK = """\
---
id: JF-904
---

<!-- SECTION:DESCRIPTION:BEGIN -->
outer
<!-- SECTION:NOTES:BEGIN -->
inner
<!-- SECTION:NOTES:END -->
still outer, managed
<!-- SECTION:DESCRIPTION:END -->
raw tail after the outer close
"""


# --- Managed-surface rules on fixtures -----------------------------------


def test_clean_task_has_no_orphans():
    assert audit.orphan_spans(CLEAN_TASK) == []


def test_raw_tail_flags_with_line_count_and_preview():
    spans = audit.orphan_spans(RAW_TAIL_TASK)
    # The run is exactly the appended line: the DOD:END marker before it is
    # covered, so nothing rides along (the blank-plus-scaffold-heading run
    # shape is the unclosed fixture's, below).
    assert spans == [(16, 17, ["CLOSED 2026-10-05 by the orchestrator: raw tail."])]


def test_unclosed_begin_is_managed_to_eof_but_earlier_junk_still_flags():
    spans = audit.orphan_spans(UNCLOSED_TASK)
    # Span bounds are the maximal uncovered run: the leading blank (L4) and
    # the bare scaffold heading (L5) ride along, real_lines carries only the
    # real content line.
    assert spans == [(4, 8, ["raw junk before the unclosed region"])]


def test_stray_end_is_scaffolding_and_later_content_still_flags():
    spans = audit.orphan_spans(STRAY_END_TASK)
    assert spans == [(8, 9, ["content after a stray END is still raw"])]


def test_dated_duplicate_scaffold_heading_is_real_orphan():
    # Exact-match exclusion: '## Implementation Notes (2026-09-27)' is the
    # JF-853 duplicated-section pathology and must keep flagging.
    spans = audit.orphan_spans(DATED_DUPLICATE_TASK)
    assert spans == [(
        10, 13,
        ["## Implementation Notes (2026-09-27)",
         "a duplicated section is the raw-tail pathology"],
    )]


def test_nested_regions_cover_the_whole_span():
    spans = audit.orphan_spans(NESTED_TASK)
    assert spans == [(12, 13, ["raw tail after the outer close"])]


def test_whitespace_only_orphan_is_clean():
    assert audit.orphan_spans(CLEAN_TASK + "   \n\t\n\n") == []


# --- Directory census and the --check exit contract ----------------------


def test_audit_dir_reports_only_files_with_orphans(tmp_path):
    (tmp_path / "b_dirty.md").write_text(RAW_TAIL_TASK)
    (tmp_path / "a_clean.md").write_text(CLEAN_TASK)
    scanned, findings, unreadable, marker_bad = audit.audit_dir(tmp_path)
    assert scanned == 2
    assert list(findings) == ["b_dirty.md"]
    assert unreadable == []
    assert marker_bad == {}


def test_check_exits_one_on_dirty_and_zero_on_clean(tmp_path, capsys):
    (tmp_path / "dirty.md").write_text(RAW_TAIL_TASK)
    assert audit.main(["--check", "--tasks-dir", str(tmp_path)]) == 1
    out = capsys.readouterr().out
    assert "FAIL" in out
    assert "dirty.md" in out
    assert "CLOSED 2026-10-05" in out

    clean_dir = tmp_path / "clean"
    clean_dir.mkdir()
    (clean_dir / "ok.md").write_text(CLEAN_TASK)
    assert audit.main(["--check", "--tasks-dir", str(clean_dir)]) == 0
    assert "PASS" in capsys.readouterr().out


def test_census_mode_always_exits_zero(tmp_path):
    (tmp_path / "dirty.md").write_text(RAW_TAIL_TASK)
    assert audit.main(["--tasks-dir", str(tmp_path)]) == 0


def test_missing_tasks_dir_fails():
    assert audit.main(["--check", "--tasks-dir", "/nonexistent/jf854"]) == 1


# --- Marker pathologies and unreadable files -----------------------------


def test_marker_imbalance_flags_unclosed_and_stray_ends():
    assert audit.marker_imbalance(CLEAN_TASK) == 0
    assert audit.marker_imbalance(UNCLOSED_TASK) == 1
    assert audit.marker_imbalance(STRAY_END_TASK) == -1


def test_audit_dir_reports_unreadable_files_and_check_fails(tmp_path, capsys):
    (tmp_path / "ok.md").write_text(CLEAN_TASK)
    (tmp_path / "junk.md").write_bytes(b"\xff\xfe not utf-8")
    scanned, findings, unreadable, marker_bad = audit.audit_dir(tmp_path)
    assert scanned == 2
    assert findings == {}
    assert unreadable == ["junk.md"]
    assert marker_bad == {}
    # An unauditable file must fail the gate by name, not crash or pass.
    assert audit.main(["--check", "--tasks-dir", str(tmp_path)]) == 1
    assert "junk.md: not valid UTF-8" in capsys.readouterr().out


def test_census_mode_lists_marker_pathologies(tmp_path, capsys):
    (tmp_path / "unclosed.md").write_text(UNCLOSED_TASK)
    (tmp_path / "clean.md").write_text(CLEAN_TASK)
    assert audit.main(["--tasks-dir", str(tmp_path)]) == 0
    out = capsys.readouterr().out
    assert "Marker pathologies" in out
    assert "unclosed.md: 1 unclosed BEGIN" in out
    assert "clean.md" not in out.split("Marker pathologies")[1]


# --- The real tree: exactly the JF-853-parked set, nothing else ----------


def test_real_tree_raw_files_are_exactly_the_jf853_parked_set():
    scanned, findings, unreadable, marker_bad = audit.audit_dir(TASKS_DIR)
    assert scanned > 0, f"no task files found in {TASKS_DIR}"
    assert not unreadable, f"unauditable task file(s): {unreadable}"
    raw = set(findings)
    unknown = raw - KNOWN_RAW_PARKED
    assert not unknown, (
        f"new raw-tail file(s) in backlog/tasks: {sorted(unknown)} - the "
        "backlog MCP drops this content on rewrite; fold it into a managed "
        "region instead"
    )
    parked_present = {n for n in KNOWN_RAW_PARKED if (TASKS_DIR / n).exists()}
    parked_folded = KNOWN_RAW_PARKED - parked_present
    # While ANY parked file remains, a row whose file already left
    # backlog/tasks/ is a same-change merge slip, not history: delete the
    # row in the change that folded the file (JF-853). Once ALL are folded
    # the set is inert and this guard self-heals green; prune the rows then.
    assert not (parked_present and parked_folded), (
        f"stale KNOWN_RAW_PARKED rows for already-folded files: "
        f"{sorted(parked_folded)} - delete the rows in the same change "
        "that folded the files (JF-853)"
    )
    assert raw == parked_present, (
        f"raw set drifted from the parked set: raw={sorted(raw)} vs "
        f"parked={sorted(parked_present)} - update KNOWN_RAW_PARKED in "
        "the same change that normalized a parked file (JF-853)"
    )

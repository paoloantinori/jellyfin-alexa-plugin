"""JF-854 + JF-856: the pytest harness for the backlog orphan and slug audits.

scripts/audit_backlog_orphans.py is the repo's only raw-tail detector: the
backlog MCP silently drops content that sits outside its managed marker
regions when it rewrites a task file (seven-occurrence gotcha). The harness
pins the extractor's managed-region rules on tmp_path fixtures (frontmatter,
BEGIN/END regions, nesting, unclosed-BEGIN-to-EOF, stray END, scaffold
headings, whitespace-only runs) and the --check exit contract, then walks the
REAL backlog/tasks tree: the raw-tail set must equal KNOWN_RAW_PARKED (the
empty steady state since JF-853 folded the last three pathological files on
2026-10-10); a new raw file is fixed or folded, never parked silently.

JF-856 adds the slug-length guard: the CLI regenerates each filename from the
frontmatter id + title on the next save and DELETES the file when the
regenerated name passes 255 bytes (ENAMETOOLONG, rename-before-write). The
harness pins the sanitizer's character classes, the UTF-8 byte budget, the
--check contract for new overlength files and stale allowlist rows, and the
real-tree set: exactly the four KNOWN_OVERLONG founders, nothing else.
"""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))

import audit_backlog_orphans as audit  # noqa: E402

# The script owns the layout constant; the guard must pin the same tree CI
# audits, not a recomputed copy of it.
TASKS_DIR = audit.DEFAULT_TASKS_DIR

# JF-853 owned these three (all folded 2026-10-10, see below): each carried
# a marker pathology whose fold was PROVEN destructive before normalization
# (jf-637 0->5 lines lost, jf-643 0->31, jf-781 3->46 on the sweep's
# roundtrip battery), so their orphans stayed raw BY EVIDENCE until the
# markers were normalized and the content folded.
# When a member leaves backlog/tasks/, delete its row in the same change;
# once all three are folded the parked set below is empty and this guard is
# green with no further edits.
# JF-853 folded all three IN PLACE (same filenames) on 2026-10-10, so the
# rows were pruned in that change window per the same-change rule; the set
# is now the empty steady state. A future raw file must NEVER be parked here
# silently: fix or fold it instead (the MCP drops raw tails on rewrite).
KNOWN_RAW_PARKED: set[str] = set()


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


# --- JF-856 slug-guard fixtures -------------------------------------------


def task_md(ident, title):
    """A minimal task file the way the MCP serializer writes one."""
    return (f"---\nid: {ident}\ntitle: {title}\nstatus: To Do\n---\n\n"
            "## Description\n")


# 250 ASCII bytes of title + "JF-902 - " prefix + ".md" = 262 bytes: over.
LONG_TITLE = "A" * 250

# 125 Cyrillic chars = 250 UTF-8 bytes: the SAME title is far below the
# 255-character mark and still over the byte budget. Pins bytes-vs-chars.
MULTIBYTE_TITLE = chr(0x0444) * 125  # Cyrillic small ef

# Real-tree names that regenerate byte-identically; each covers one shape of
# the rule (plain/quoted/folded title, parens, dots in type names, dotted id,
# non-jf prefix, accented letters, apostrophe plus colon in the folded title).
SLUG_IDENTITY_SAMPLE = [
    "jf-102 - Apply-fuzzy-match-before-disambiguation-to-all-search-handlers.md",
    "jf-106 - German-de-DE-phonetic-synonyms-for-English-names.md",
    "jf-134.1 - Feature-flags-for-intent-groups-radio-podcasts-live-TV-etc..md",
    "jf-115.1 - Create-reusable-APL-list-template-in-AplHelper.md",
    "draft-5.1 - JF-139-B-Integration-tests-for-per-user-library-filtering.md",
    "task-high.1 - Annuncio-episodio-data-estesa-al-posto-del-doppio-progressivo-it-IT.md",
    "jf-856 - Mechanical-slug-length-guard-for-the-backlog-ENAMETOOLONG-deletion-class-the-audit-computes-every-tasks-regenerated-slug-and-fails-over-255-bytes.md",
]

# The em-dash survives sanitization and costs three UTF-8 bytes; built from a
# codepoint so this file stays free of the banned character.
EMDASH = chr(0x2014)

# A task file without a title key: the one shape the slug guard cannot
# compute a name for, so the guard must surface it instead of passing it.
TITLELESS_TASK = "---\nid: JF-900\nstatus: To Do\n---\n\n## Description\n"


@pytest.fixture(scope="module")
def real_tree():
    """One audit pass over the real backlog tree, shared by the real-tree
    guards (three scans of 852 files per run would be waste)."""
    return audit.audit_dir(TASKS_DIR)


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
    scanned, findings, unreadable, marker_bad, slug_bad, slug_skip = \
        audit.audit_dir(tmp_path)
    assert scanned == 2
    assert list(findings) == ["b_dirty.md"]
    assert unreadable == []
    assert marker_bad == {}
    assert slug_bad == {}
    assert slug_skip == []  # fixtures carry parseable id and title


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
    scanned, findings, unreadable, marker_bad, slug_bad, slug_skip = \
        audit.audit_dir(tmp_path)
    assert scanned == 2
    assert findings == {}
    assert unreadable == ["junk.md"]
    assert marker_bad == {}
    assert slug_bad == {}
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


def test_real_tree_raw_files_are_exactly_the_jf853_parked_set(real_tree):
    scanned, findings, unreadable, marker_bad, slug_bad, slug_skip = real_tree
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


# --- JF-856: the slug rule itself -----------------------------------------


def test_separator_class_becomes_one_dash():
    assert audit.sanitize_title('a<b>c:d"e/f\\g|h?i*j') == "a-b-c-d-e-f-g-h-i-j"


def test_delete_class_is_removed_in_place():
    assert audit.sanitize_title("P!nk (live) user's, n+1=2 [x]{y}; z") == \
        "Pnk-live-users-n12-xy-z"


def test_dash_runs_collapse_and_edge_dashes_trim():
    assert audit.sanitize_title("  a ---  b -- ") == "a-b"


def test_unicode_letters_and_symbols_pass_through_verbatim():
    # No lowercasing, no transliteration: Devanagari, accents, the em-dash
    # and the tilde ride into the filename as-is (and cost UTF-8 bytes).
    title = f"филм caffè {EMDASH} 100% _under_score ~tilde"
    assert audit.sanitize_title(title) == f"филм-caffè-{EMDASH}-100-_under_score-~tilde"


def test_whitespace_class_is_javascript_s_not_python_s():
    # Probed on the installed v1.44.0 binary (review of this diff): the CLI
    # dashes U+FEFF but keeps U+0085 and the \\x1c-\\x1f controls verbatim,
    # where Python \\s is the reverse; the port pins the JS class so a
    # boundary title computes the same byte length the CLI will rename to.
    nel, ctrl, bom, nbsp = chr(0x85), chr(0x1C), chr(0xFEFF), chr(0xA0)
    assert audit.sanitize_title(f"nel-{nel}x") == f"nel-{nel}x"
    assert audit.sanitize_title(f"a{ctrl}c") == f"a{ctrl}c"
    assert audit.sanitize_title(f"bom{bom}x") == "bom-x"
    assert audit.sanitize_title(f"nb{nbsp}x") == "nb-x"


def test_regenerated_filename_lowercases_id_and_joins():
    assert audit.regenerated_filename("JF-856", "X y", "task") == "jf-856 - X-y.md"


def test_regenerated_filename_uses_configured_prefix_for_numeric_id():
    assert audit.regenerated_filename("123", "X", "task") == "task-123 - X.md"
    # This repo configures task_prefix "jf" (backlog/config.yml line 16):
    # an unprefixed id regenerates onto jf-123, not task-123.
    assert audit.regenerated_filename("123", "X", "jf") == "jf-123 - X.md"


def test_configured_default_prefix_reads_project_config(tmp_path):
    (tmp_path / "config.yml").write_text(
        'project_name: "x"\ntask_prefix: "jf"\n', encoding="utf-8")
    assert audit.configured_default_prefix(tmp_path / "tasks") == "jf"
    # Upstream default when the key is absent or the config missing.
    assert audit.configured_default_prefix(tmp_path) == "task"


def test_frontmatter_field_reads_plain_quoted_and_folded():
    assert audit.frontmatter_field(task_md("JF-1", "plain title"), "title") == "plain title"
    assert audit.frontmatter_field(task_md("JF-1", "'quoted ''x'''"), "title") == "quoted 'x'"
    folded = "---\nid: JF-1\ntitle: >-\n  line one\n  line two\nstatus: To Do\n---\n"
    assert audit.frontmatter_field(folded, "title") == "line one line two"
    assert audit.frontmatter_field(folded, "id") == "JF-1"
    assert audit.frontmatter_field(folded, "absent") is None


def test_overlength_report_counts_utf8_bytes_not_chars():
    assert audit.overlength_report(task_md("JF-902", MULTIBYTE_TITLE),
                                   "task") > audit.MAX_FILENAME_BYTES


def test_overlength_report_is_none_without_title():
    assert audit.overlength_report(TITLELESS_TASK, "task") is None


def test_real_sample_names_regenerate_byte_identically():
    prefix = audit.configured_default_prefix(TASKS_DIR)
    for name in SLUG_IDENTITY_SAMPLE:
        text = (TASKS_DIR / name).read_text(encoding="utf-8")
        ident = audit.frontmatter_field(text, "id")
        title = audit.frontmatter_field(text, "title")
        assert audit.regenerated_filename(ident, title, prefix) == name, name


# --- JF-856: the --check contract for overlength filenames -----------------


def test_check_fails_on_new_overlength_and_names_the_remedy(tmp_path, capsys):
    (tmp_path / "long.md").write_text(task_md("JF-902", LONG_TITLE))
    assert audit.main(["--check", "--tasks-dir", str(tmp_path)]) == 1
    out = capsys.readouterr().out
    assert "long.md" in out
    assert "262 bytes" in out
    assert "ENAMETOOLONG" in out
    assert "shorten the title" in out and "KNOWN_OVERLONG" in out


def test_check_passes_parked_overlength_with_listing(tmp_path, capsys, monkeypatch):
    (tmp_path / "long.md").write_text(task_md("JF-902", LONG_TITLE))
    monkeypatch.setattr(audit, "KNOWN_OVERLONG", frozenset({"long.md"}))
    assert audit.main(["--check", "--tasks-dir", str(tmp_path)]) == 0
    out = capsys.readouterr().out
    assert "KNOWN (listed, not failing" in out
    assert "PASS" in out


def test_check_fails_on_stale_known_rows_per_same_change_rule(capsys,
                                                              monkeypatch):
    # Stale rows are judged against the tree the set describes, so this run
    # targets the real tree with a monkeypatched allowlist: one row for an
    # existing but non-overlength file (shortened), one for a missing file.
    real = next(TASKS_DIR.glob("jf-102*.md"))
    monkeypatch.setattr(audit, "KNOWN_OVERLONG",
                        frozenset({real.name, "jf-9999 - gone.md"}))
    assert audit.main(["--check"]) == 1
    out = capsys.readouterr().out
    assert "stale KNOWN_OVERLONG" in out
    assert "no longer regenerates over" in out
    assert "left backlog/tasks" in out


def test_census_lists_slug_findings_without_failing(tmp_path, capsys):
    (tmp_path / "long.md").write_text(task_md("JF-905", LONG_TITLE))
    assert audit.main(["--tasks-dir", str(tmp_path)]) == 0
    out = capsys.readouterr().out
    assert "NOT parked" in out
    assert "262 bytes" in out


def test_slug_guard_fails_on_unparseable_title(tmp_path, capsys):
    # Nothing may sit outside the guard: a title the parser cannot read is
    # exactly where a long title would hide, so it gates like unreadable.
    (tmp_path / "notitle.md").write_text(TITLELESS_TASK)
    assert audit.main(["--check", "--tasks-dir", str(tmp_path)]) == 1
    out = capsys.readouterr().out
    assert "cannot audit 1 file(s)" in out
    assert "notitle.md" in out


def test_split_known_fresh_partitions_by_allowlist():
    slug_bad = {"a.md": 300, "b.md": 260}
    assert audit.split_known_fresh(slug_bad, {"a.md"}) == \
        ({"a.md": 300}, {"b.md": 260})
    assert audit.split_known_fresh(slug_bad, frozenset()) == \
        ({}, {"a.md": 300, "b.md": 260})


def test_stale_allowlist_rows_classifies_shortened_gone_and_unreadable(tmp_path):
    (tmp_path / "short.md").write_text(task_md("JF-904", "now fine"))
    allowlist = frozenset({"short.md", "left.md", "junk.md"})
    shortened, gone = audit.stale_allowlist_rows({}, ["junk.md"], [], tmp_path,
                                                 allowlist)
    assert shortened == ["short.md"]
    assert gone == ["left.md"]
    # A row still earning its place is not stale, and a row on an unreadable
    # file is that file's own failure, not a stale row.
    shortened, gone = audit.stale_allowlist_rows(
        {"short.md": 300}, ["junk.md"], [], tmp_path, allowlist)
    assert shortened == []
    assert gone == ["left.md"]
    # A row on a file the guard cannot parse is neither: the unparseable
    # FAIL is the finding, and the row must not be pruned on its back.
    shortened, gone = audit.stale_allowlist_rows(
        {}, ["junk.md"], ["short.md"], tmp_path, allowlist)
    assert shortened == []
    assert gone == ["left.md"]


# --- JF-856: the real tree: exactly the four founders, nothing else --------


def test_real_tree_overlength_files_are_exactly_the_known_set(real_tree):
    scanned, findings, unreadable, marker_bad, slug_bad, slug_skip = real_tree
    assert scanned > 0, f"no task files found in {TASKS_DIR}"
    assert not slug_skip, (
        f"slug guard could not parse id/title in: {sorted(slug_skip)} - a "
        "file outside the guard is a silent ENAMETOOLONG hazard; extend the "
        "frontmatter parser instead of leaving it skipped"
    )
    fresh = set(slug_bad) - audit.KNOWN_OVERLONG
    assert not fresh, (
        f"new overlength file(s) in backlog/tasks: {sorted(fresh)} - the "
        "next Backlog.md edit deletes them (ENAMETOOLONG); shorten the "
        "title, or knowingly add the file to KNOWN_OVERLONG in "
        "scripts/audit_backlog_orphans.py (hand-edit-only regime)"
    )
    for name in audit.KNOWN_OVERLONG:
        assert name in slug_bad, (
            f"stale KNOWN_OVERLONG row: {name} no longer regenerates over "
            f"{audit.MAX_FILENAME_BYTES} bytes (or the file left "
            "backlog/tasks) - delete the row in the same change"
        )
        assert slug_bad[name] > audit.MAX_FILENAME_BYTES

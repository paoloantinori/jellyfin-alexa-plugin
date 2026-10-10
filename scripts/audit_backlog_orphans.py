#!/usr/bin/env python3
"""Audit Backlog.md task files for orphan content outside the managed scaffold.

The backlog MCP serializes task files through managed marker regions
(``<!-- SECTION:DESCRIPTION:BEGIN -->`` ... ``<!-- SECTION:DESCRIPTION:END -->``,
the DOD block, ...) plus a fixed set of scaffold headings. Content that sits
OUTSIDE those regions, such as a raw tail appended after the last managed
block or a duplicated Implementation Notes section, is silently DROPPED by the
MCP's rewrite path. That data-loss gotcha has hit this repo seven times by
2026-10. This is the repo's only raw-tail detector (JF-854): it extracts the
managed surface exactly the way the fold sweep's census did and reports what
remains, so raw tails can never silently accumulate again.

Managed-region rules (JF-854, from the fold-sweep census.py/census2.py):
  - A leading YAML frontmatter block (``---`` ... ``---``) is managed.
  - Any ``<!-- ...:BEGIN -->`` line pushes a region and the matching
    ``<!-- ...:END -->`` line pops it; the whole span, marker lines included,
    is managed. Regions nest.
  - An unclosed BEGIN is managed to EOF (conservative: treat the tail as
    owned by a live region rather than flag it as dropped data). The cost of
    that conservatism: the audit is blind to everything past an unclosed
    BEGIN, so the census also reports files with unbalanced marker counts,
    which are exactly the normalization targets of JF-853.
  - A stray END at depth 0 is scaffolding, not content; every marker line is
    managed regardless.
  - Blank or whitespace-only lines outside the managed surface are not
    content.
  - The MCP scaffold headings (## Description, ## Acceptance Criteria, ...)
    sit outside the marker regions by design; a bare occurrence is not an
    orphan. Matching is EXACT: ``## Implementation Notes (2026-09-27)``, the
    duplicated-section pathology itself (JF-853), is real orphan content.

Modes:
  (default)  Census: one block per file carrying orphan content (file, orphan
             line count, first orphan line preview), then a summary line.
             Informational only; exits 0 unless the tasks dir is missing.
  --check    CI gate: same scan over backlog/tasks/ (backlog/completed/ is
             out of scope, folded tasks are archived history); prints one
             finding per file and exits 1 when any file carries real
             (non-whitespace) orphan content, 0 when clean.

Exit code: 0 clean (or census mode), 1 orphan content found, an unreadable
(non-UTF-8) task file in --check mode, or the tasks directory is missing.
"""

import argparse
import re
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_TASKS_DIR = SCRIPT_DIR.parent / "backlog" / "tasks"

MARKER = re.compile(r"^\s*<!--\s*(?:.*?)\s*:\s*(BEGIN|END)\s*-->\s*$")

# Bare MCP scaffold headings: they sit outside the marker regions by design.
# Exact match on purpose: a dated duplicate is the raw-tail pathology itself.
SCAFFOLD_HEADINGS = frozenset({
    "## Description",
    "## Acceptance Criteria",
    "## Definition of Done",
    "## Implementation Notes",
    "## Implementation Plan",
    "## Final Summary",
    "## Comments",
    "## Notes",
})

PREVIEW_WIDTH = 100


def orphan_spans(text):
    """Return the maximal orphan runs in one task file's text.

    Each run is a (first_line_no, last_line_no_exclusive, real_lines) tuple
    with 1-based line numbers and real_lines the run's non-blank,
    non-scaffold-heading lines, whitespace-stripped. A run is a maximal
    stretch of lines after the frontmatter that no managed region covers and
    that carries at least one real content line.
    """
    lines = text.split("\n")
    n = len(lines)
    covered = [False] * n

    fm_end = 0
    if lines[0].strip() == "---":
        for i in range(1, n):
            if lines[i].strip() == "---":
                fm_end = i + 1
                break
    for i in range(fm_end):
        covered[i] = True

    stack = []
    for i, line in enumerate(lines):
        m = MARKER.match(line)
        if not m:
            continue
        covered[i] = True  # marker lines are scaffolding even when stray
        if m.group(1) == "BEGIN":
            stack.append(i)
        elif stack:  # matching END: the whole span incl. markers is managed
            for j in range(stack.pop(), i + 1):
                covered[j] = True
    for s in stack:  # unclosed BEGIN: managed to EOF
        for j in range(s, n):
            covered[j] = True

    spans = []
    i = fm_end
    while i < n:
        if covered[i]:
            i += 1
            continue
        j = i
        while j < n and not covered[j]:
            j += 1
        real = [ln.strip() for ln in lines[i:j]
                if ln.strip() and ln.strip() not in SCAFFOLD_HEADINGS]
        if real:
            spans.append((i + 1, j, real))
        i = j
    return spans


def marker_imbalance(text):
    """BEGIN count minus END count; nonzero marks a marker pathology.

    A positive delta blinds the audit to everything past the unclosed BEGIN
    (the conservative to-EOF rule); a negative delta is a stray END.
    """
    depth = 0
    for line in text.split("\n"):
        m = MARKER.match(line)
        if m:
            depth += 1 if m.group(1) == "BEGIN" else -1
    return depth


def audit_dir(tasks_dir):
    """Census a tasks directory in one pass.

    Returns (scanned, findings, unreadable, marker_bad): the *.md count, the
    {file name: [Span, ...]} files carrying orphan runs, the file names that
    are not valid UTF-8, and {file name: BEGIN-minus-END} for files with
    unbalanced markers.
    """
    scanned = 0
    findings = {}
    unreadable = []
    marker_bad = {}
    for path in sorted(tasks_dir.glob("*.md")):
        scanned += 1
        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            unreadable.append(path.name)
            continue
        spans = orphan_spans(text)
        if spans:
            findings[path.name] = spans
        delta = marker_imbalance(text)
        if delta:
            marker_bad[path.name] = delta
    return scanned, findings, unreadable, marker_bad


def span_line_count(spans):
    return sum(len(real) for _, _, real in spans)


def print_span_lines(spans):
    for first, last, real in spans:
        preview = real[0][:PREVIEW_WIDTH]
        print(f"    L{first}-{last}: first: {preview!r}")


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Report content outside the backlog MCP's managed regions "
                    "(JF-854 raw-tail guard).")
    parser.add_argument(
        "--check", action="store_true",
        help="exit 1 when any task file carries real orphan content (CI gate)")
    parser.add_argument(
        "--tasks-dir", type=Path, default=DEFAULT_TASKS_DIR,
        help="Backlog.md tasks directory to scan (default: %(default)s)")
    args = parser.parse_args(argv)

    if not args.tasks_dir.is_dir():
        print(f"FAIL: tasks directory not found: {args.tasks_dir}")
        return 1

    scanned, findings, unreadable, marker_bad = audit_dir(args.tasks_dir)
    total_lines = sum(span_line_count(spans) for spans in findings.values())

    for name in unreadable:
        print(f"FAIL: {name}: not valid UTF-8, cannot audit")

    if args.check:
        if findings:
            print(f"FAIL: {len(findings)} task file(s) carry real orphan "
                  f"content outside the managed scaffold ({total_lines} "
                  f"line(s)); the backlog MCP drops it on rewrite:")
            for name, spans in findings.items():
                print(f"  {name}: {span_line_count(spans)} orphan line(s)")
                print_span_lines(spans)
            return 1
        if unreadable:
            return 1
        print(f"PASS: no orphan content in {scanned} task files")
        return 0

    print(f"Scanning {scanned} task files in {args.tasks_dir}")
    for name, spans in findings.items():
        print(f"\n### {name}")
        print(f"    {len(spans)} span(s), {span_line_count(spans)} orphan line(s)")
        print_span_lines(spans)
    print(f"\n{'='*60}")
    print(f"{len(findings)} file(s) with orphan content, {total_lines} orphan "
          f"line(s) / {scanned} scanned")
    if marker_bad:
        print(f"Marker pathologies (unbalanced BEGIN/END; the audit is blind "
              f"past an unclosed BEGIN): {len(marker_bad)} file(s)")
        for name, delta in marker_bad.items():
            kind = f"{delta} unclosed BEGIN" if delta > 0 else f"{-delta} stray END"
            print(f"  {name}: {kind}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

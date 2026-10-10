#!/usr/bin/env python3
"""Audit Backlog.md task files for orphan content and overlength filenames.

Part 1 (JF-854, orphan guard). The backlog MCP serializes task files through
managed marker regions
(``<!-- SECTION:DESCRIPTION:BEGIN -->`` ... ``<!-- SECTION:DESCRIPTION:END -->``,
the DOD block, ...) plus a fixed set of scaffold headings. Content that sits
OUTSIDE those regions, such as a raw tail appended after the last managed
block or a duplicated Implementation Notes section, is silently DROPPED by the
MCP's rewrite path. That data-loss gotcha has hit this repo seven times by
2026-10. This is the repo's only raw-tail detector (JF-854): it extracts the
managed surface exactly the way the fold sweep's census did and reports what
remains, so raw tails can never silently accumulate again.

Part 2 (JF-856, slug-length guard). On every save the Backlog.md CLI
regenerates each task's filename from its frontmatter id and title and
renames the file BEFORE writing. A regenerated name over the kernel's
255-byte filename limit fails with ENAMETOOLONG, and by then the original
file is already unlinked (reproduced live on CLI v1.44.0; the details and
the reproducers are in the JF-853 task). The guard computes the regenerated
name for every file and fails --check on any file whose name would exceed
the limit, so a new long-titled task cannot land without either shortening
the title or knowingly accepting the hand-edit-only regime (the
KNOWN_OVERLONG allowlist below).

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
             line count, first orphan line preview), then a summary line with
             the marker pathologies and the slug-length findings. Informational
             only; exits 0 unless the tasks dir is missing.
  --check    CI gate: same scan over backlog/tasks/ (backlog/completed/ is
             out of scope, folded tasks are archived history); prints one
             finding per file. Exits 1 when any file carries real
             (non-whitespace) orphan content, when a NEW file's regenerated
             filename exceeds 255 bytes (files parked in KNOWN_OVERLONG are
             listed but do not fail), when a KNOWN_OVERLONG row is stale
             (its file was shortened or left backlog/tasks: prune the row in
             the same change), when a task file cannot be audited (not valid
             UTF-8, or its id/title cannot be parsed: nothing may sit
             outside the guards), or exits 0 when clean.

Exit code: 0 clean (or census mode), 1 any of the failures above, or the
tasks directory is missing.
"""

import argparse
import re
import sys
from pathlib import Path
from typing import NamedTuple

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


# --- Slug-length guard (JF-856) -------------------------------------------
#
# The naming rule below is a verbatim port of Backlog.md CLI v1.44.0:
# src/file-system/operations.ts saveTask builds
#   `${idForFilename(taskId)} - ${this.sanitizeFilename(task.title)}.md`
# where idForFilename is id.toLowerCase() (src/utils/prefix-config.ts) and
# sanitizeFilename applies, in order: the separator class to "-", the delete
# class removed, whitespace runs to "-", dash runs collapsed, edge dashes
# trimmed. Everything else passes through untouched: Unicode letters
# (Devanagari included), ".", "_", "~", the em-dash, arrows. That is why the
# budget counts UTF-8 bytes of the WHOLE filename, not characters.
#
# The port is verified three ways (2026-10-10): against the v1.44.0 source;
# against the installed v1.44.0 binary in a scratch project (created names
# byte-identical; a >255-byte title reproduced ENAMETOOLONG with the original
# file already deleted); against this tree, where 799 of 852 names regenerate
# byte-identically and every other file is a known drift case (hand-shortened
# names on the four overlong files, titles hand-edited after creation,
# jf-697 whose frontmatter id is JF-696, three names missing the " - "
# joiner spaces). There is NO leading-id strip in the rule: files like
# jf-770 ("jf-770 - JF-770-...") prove a title starting with its own id
# regenerates the doubled prefix.

SLUG_SEPARATOR = re.compile(r'[<>:"/\\|?*]')
SLUG_DELETE = re.compile(r"['(),!@#$%^&+=\[\]{};]")
# The CLI runs on JavaScript, so the whitespace class is JS \s, pinned
# explicitly: Python \s diverges on U+0085 and \x1c-\x1f (Python-only
# whitespace; the installed v1.44.0 binary keeps those verbatim, probed) and
# would miss U+FEFF (the CLI dashes it).
SLUG_WHITESPACE = re.compile(
    "[\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f"
    "\\u205f\\u3000\\ufeff]+")
SLUG_DASH_RUN = re.compile(r"-+")
SLUG_EDGE_DASH = re.compile(r"^-|-$")

MAX_FILENAME_BYTES = 255

# Founding members of the hand-edit-only regime (JF-853): on each of these the
# next Backlog.md edit regenerates a name over 255 bytes and DELETES the file.
# They are parked knowingly: --check lists them but does not fail on them.
# Same-change discipline, as for the orphan allowlist in the test: when a
# member's title is shortened or the file leaves backlog/tasks, delete its row
# in the same change (the stale-row branch below fails the gate otherwise);
# a NEW overlong file may join only through a deliberate edit of this set,
# never by silence. Byte lengths move with title edits, so a row is keyed by
# the on-disk filename, not by the current length.
KNOWN_OVERLONG = frozenset({
    "jf-724 - JF-724-two-pre-existing-ledger-concurrency-gaps-the-unguarded-panel-enumerations-and-the-cross-user-row-ownership.md",
    "jf-771 - hi-IN-film-carried-short-forms-beyond-dekho-khojo-tail-Fallback-and-chalao-stolen-by-PlaySong-bare-song-chalao.md",
    "jf-774 - the-two-root-split-broke-three-same-root-self-healing-shapes-cross-root-scan-recency-stale-prewrite-shadow-debris-masking-valid-transient.md",
    "jf-782 - the-JF-775-timing-residuals-probe-vs-verdict-liveness-straddle-and-foreign-ticks-mid-registration.md",
})

# YAML frontmatter shapes the serializer writes (all 852 files at the time of
# writing): plain scalars, single-quoted ('' is an escaped quote), and block
# scalars (>- and friends, optionally with an indent digit). Double-quoted
# values carry backslash escapes; only \" and \\ occur in this repo, anything
# else stays as written rather than being silently mangled.
FM_BLOCK_HEADER = re.compile(r"^([>|])\d*[+-]?$")


def frontmatter_end(lines):
    """Index one past the closing ``---`` of the leading frontmatter block,
    or None when the text does not start with one. The one definition of
    where the managed header ends (orphan_spans covers it, the slug guard
    reads fields inside it)."""
    if not lines or lines[0].strip() != "---":
        return None
    for i in range(1, len(lines)):
        if lines[i].strip() == "---":
            return i + 1
    return None


def frontmatter_field(text, field):
    """Extract a top-level frontmatter scalar (``id``, ``title``) as the
    MCP serializer writes it. Returns the decoded string, or None when the
    field is absent or empty (an empty-title task regenerates a tiny name
    and is out of the guard's class)."""
    lines = text.split("\n")
    end = frontmatter_end(lines)
    if end is None:
        return None
    key = field + ": "
    for i in range(1, end):
        if not lines[i].startswith(key):
            continue
        value = lines[i][len(key):].rstrip()
        header = FM_BLOCK_HEADER.match(value)
        if header:
            body = []
            for ln in lines[i + 1:]:
                if not ln.strip():
                    body.append("")  # blank line keeps paragraph structure
                    continue
                if not ln[:1].isspace():
                    break  # next top-level key: block scalar ended
                body.append(ln.strip())
            joiner = "\n" if header.group(1) == "|" else " "
            return joiner.join(body).strip() or None
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
            inner = value[1:-1]
            if value[0] == "'":
                return inner.replace("''", "'") or None
            return inner.replace('\\"', '"').replace("\\\\", "\\") or None
        return value or None
    return None


def sanitize_title(title):
    """Port of FileOperations.sanitizeFilename (v1.44.0), see block above."""
    s = SLUG_SEPARATOR.sub("-", title)
    s = SLUG_DELETE.sub("", s)
    s = SLUG_WHITESPACE.sub("-", s)
    s = SLUG_DASH_RUN.sub("-", s)
    return SLUG_EDGE_DASH.sub("", s)


def regenerated_filename(ident, title, default_prefix):
    """The filename the CLI writes on the next save of this task.

    saveTask takes the prefix from the id itself (extractAnyPrefix) and only
    falls back to the configured default, so an id like "JF-856" keeps its
    prefix; the id lands in the filename lowercased. An id without an alpha
    prefix gets the project's configured prefix (this repo: task_prefix "jf"
    in backlog/config.yml; upstream default "task")."""
    ident = ident.strip()
    if not re.match(r"^[A-Za-z]+-", ident):
        ident = default_prefix + "-" + ident
    return "{} - {}.md".format(ident.lower(), sanitize_title(title))


def configured_default_prefix(tasks_dir):
    """The fallback prefix for ids that carry no alpha prefix, read from the
    project config sitting beside the tasks dir (YAML key ``task_prefix``,
    upstream: config.prefixes.task, default "task"). Only the plain flat key
    is written by the projects we audit; anything else falls back to the
    upstream default."""
    try:
        text = (tasks_dir.parent / "config.yml").read_text(encoding="utf-8")
    except OSError:
        return "task"
    m = re.search(r'^task_prefix:\s*["\']?([A-Za-z][\w-]*)', text, re.MULTILINE)
    return m.group(1) if m else "task"


def overlength_report(text, default_prefix):
    """Whole-filename UTF-8 byte length of the regenerated name, or None when
    the id or title cannot be read (reported as skipped, never guessed)."""
    ident = frontmatter_field(text, "id")
    title = frontmatter_field(text, "title")
    if ident is None or title is None:
        return None
    return len(regenerated_filename(ident, title, default_prefix)
               .encode("utf-8"))


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

    fm_end = frontmatter_end(lines)
    if fm_end is None:
        fm_end = 0
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


class AuditResult(NamedTuple):
    """One pass over a tasks directory; every per-file finding by name."""

    scanned: int
    findings: dict  # name -> [Span, ...] orphan runs
    unreadable: list  # names not valid UTF-8
    marker_bad: dict  # name -> BEGIN-minus-END for unbalanced markers
    slug_bad: dict  # name -> regenerated byte length over the limit
    slug_skip: list  # names whose id/title the slug guard cannot parse


def audit_dir(tasks_dir):
    """Census a tasks directory in one pass."""
    scanned = 0
    findings = {}
    unreadable = []
    marker_bad = {}
    slug_bad = {}
    slug_skip = []
    default_prefix = configured_default_prefix(tasks_dir)
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
        length = overlength_report(text, default_prefix)
        if length is None:
            slug_skip.append(path.name)
        elif length > MAX_FILENAME_BYTES:
            slug_bad[path.name] = length
    return AuditResult(scanned, findings, unreadable, marker_bad, slug_bad,
                       slug_skip)


def split_known_fresh(slug_bad, allowlist):
    """Partition the overlength findings against the allowlist: (known,
    fresh). Known rows are the knowingly parked hand-edit-only files; fresh
    rows are new and fail the gate."""
    known = {n: b for n, b in slug_bad.items() if n in allowlist}
    fresh = {n: b for n, b in slug_bad.items() if n not in allowlist}
    return known, fresh


def stale_allowlist_rows(slug_bad, unreadable, slug_skip, tasks_dir, allowlist):
    """Allowlist rows whose file no longer qualifies: it exists but no
    longer regenerates over the limit (shortened), or it left the tasks
    directory (gone). A row on an unreadable or unparseable file stays out:
    that file already fails the gate on its own condition."""
    shortened, gone = [], []
    for name in allowlist:
        if name in slug_bad or name in unreadable or name in slug_skip:
            continue
        if (tasks_dir / name).exists():
            shortened.append(name)
        else:
            gone.append(name)
    return sorted(shortened), sorted(gone)


def span_line_count(spans):
    return sum(len(real) for _, _, real in spans)


def print_span_lines(spans):
    for first, last, real in spans:
        preview = real[0][:PREVIEW_WIDTH]
        print(f"    L{first}-{last}: first: {preview!r}")


def print_slug_section(known, fresh, skip, check_mode):
    """Render the overlength findings; the classification already happened
    in split_known_fresh / stale_allowlist_rows."""
    if fresh:
        if check_mode:
            print(f"FAIL: {len(fresh)} task file(s) whose regenerated "
                  f"filename exceeds {MAX_FILENAME_BYTES} bytes; the next "
                  f"Backlog.md edit renames the file onto it and DELETES "
                  f"the task on ENAMETOOLONG:")
            for name, length in fresh.items():
                print(f"  {name}: {length} bytes (limit "
                      f"{MAX_FILENAME_BYTES}); shorten the title, or "
                      f"knowingly add the file to KNOWN_OVERLONG "
                      f"(hand-edit-only regime)")
        else:
            print(f"Slug overlength, NOT parked (NEW; the CLI deletes the "
                  f"file on the next edit): {len(fresh)} file(s)")
            for name, length in fresh.items():
                print(f"  {name}: {length} bytes")
    if known:
        label = "KNOWN (listed, not failing; hand-edit-only regime)" \
            if check_mode else "Slug overlength, parked in KNOWN_OVERLONG"
        print(f"{label}: {len(known)} file(s)")
        for name, length in known.items():
            print(f"  {name}: {length} bytes")
    if skip:
        if check_mode:
            print(f"FAIL: slug guard cannot audit {len(skip)} file(s) (id "
                  f"or title not parseable; a long title would hide there): "
                  f"{', '.join(skip)}; extend frontmatter_field")
        else:
            print(f"Slug guard skipped {len(skip)} file(s) (id or title not "
                  f"parseable): {', '.join(skip)}")


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Report content outside the backlog MCP's managed "
                    "regions (JF-854 raw-tail guard) and filenames whose "
                    "regenerated slug exceeds 255 bytes (JF-856 ENAMETOOLONG "
                    "guard).")
    parser.add_argument(
        "--check", action="store_true",
        help="exit 1 on real orphan content, a NEW overlength filename, or "
             "a stale KNOWN_OVERLONG row (CI gate)")
    parser.add_argument(
        "--tasks-dir", type=Path, default=DEFAULT_TASKS_DIR,
        help="Backlog.md tasks directory to scan (default: %(default)s)")
    args = parser.parse_args(argv)

    if not args.tasks_dir.is_dir():
        print(f"FAIL: tasks directory not found: {args.tasks_dir}")
        return 1

    scanned, findings, unreadable, marker_bad, slug_bad, slug_skip = \
        audit_dir(args.tasks_dir)
    total_lines = sum(span_line_count(spans) for spans in findings.values())

    for name in unreadable:
        print(f"FAIL: {name}: not valid UTF-8, cannot audit")

    if args.check:
        known, fresh = split_known_fresh(slug_bad, KNOWN_OVERLONG)
        print_slug_section(known, fresh, slug_skip, check_mode=True)
        # Stale allowlist rows only make sense against the tree the set
        # describes (the CI target): on tmp or foreign dirs a missing row
        # means nothing, so fixture runs stay clean.
        if args.tasks_dir.resolve() == DEFAULT_TASKS_DIR.resolve():
            shortened, gone = stale_allowlist_rows(slug_bad, unreadable,
                                                   slug_skip,
                                                   args.tasks_dir,
                                                   KNOWN_OVERLONG)
        else:
            shortened = gone = []
        if shortened or gone:
            print("FAIL: stale KNOWN_OVERLONG row(s); prune them in the "
                  "same change that fixed the files:")
            for name in shortened:
                print(f"  {name}: the file no longer regenerates over "
                      f"{MAX_FILENAME_BYTES} bytes; delete the row")
            for name in gone:
                print(f"  {name}: the file left backlog/tasks; delete "
                      f"the row")
        failed = bool(findings or unreadable or fresh or slug_skip
                      or shortened or gone)
        if findings:
            print(f"FAIL: {len(findings)} task file(s) carry real orphan "
                  f"content outside the managed scaffold ({total_lines} "
                  f"line(s)); the backlog MCP drops it on rewrite:")
            for name, spans in findings.items():
                print(f"  {name}: {span_line_count(spans)} orphan line(s)")
                print_span_lines(spans)
        if failed:
            return 1
        pass_line = (f"PASS: no orphan content, no new overlength filenames "
                     f"in {scanned} task files")
        if known:
            pass_line += (f"; {len(known)} known overlength file(s) parked "
                          f"in KNOWN_OVERLONG")
        print(pass_line)
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
    known, fresh = split_known_fresh(slug_bad, KNOWN_OVERLONG)
    print_slug_section(known, fresh, slug_skip, check_mode=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())

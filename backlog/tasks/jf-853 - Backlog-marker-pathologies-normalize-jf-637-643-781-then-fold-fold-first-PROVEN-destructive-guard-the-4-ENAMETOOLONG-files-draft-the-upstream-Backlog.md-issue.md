---
id: JF-853
title: >-
  Backlog marker pathologies: normalize jf-637/643/781 then fold (fold-first
  PROVEN destructive), guard the 4 ENAMETOOLONG files, draft the upstream
  Backlog.md issue
status: In Progress
assignee: []
created_date: '2026-10-09 22:44'
updated_date: '2026-10-10 05:25'
labels:
  - tooling
  - tech-debt
  - backlog-hygiene
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator from the fold sweep's empirical gate (the 3 reverts + the 4 ENAMETOOLONG findings; the sweep's own report, merge a2fb7e6d).

PART 1 - the three deliberately-reverted pathological files (jf-637, jf-643, jf-781): the sweep PROVED that folding their raw orphans would make the next real MCP rewrite WORSE (jf-637 0->5 lines lost, jf-643 0->31, jf-781 3->46), because each carries a marker pathology (an unclosed SECTION:NOTES:ORCHESTRATOR-SIMPLIFY block, a stray nested NOTES:BEGIN, a legacy `<!-- NOTES:` pair) and a second Implementation Notes section flips the MCP from benign pass-through to destructive re-serialization over the pathology. Their orphans remain raw BY EVIDENCE, not omission. Hand treatment, per file: normalize the marker structure FIRST (close the unclosed region, convert/remove the legacy pair, de-nest), re-run the sweep's roundtrip battery (rt_battery.py + census scripts preserved at /var/tmp/fold_sweep/ - copy them into the task's scratch before /var/tmp is wiped) to prove 0-loss, then fold. Do NOT fold before normalizing; that is the whole lesson of the revert.

PART 2 - the four ENAMETOOLONG-class files (jf-724, jf-771, jf-774, jf-782): any backlog CLI/MCP edit on them FAILS with ENAMETOOLONG (regenerated slug exceeds 255 bytes) AND DELETES the file outright - identically at HEAD, a PRE-EXISTING upstream hazard (the destructive rewrite is updateChecklistContent's legacy swallow, per the sweep altitude agent's source read of Backlog.md). Their folds were kept on mechanical evidence only. Actions: (a) never MCP-edit these four files (hand-edit only; the sixth-occurrence memory rule already covers the recovery); (b) draft an upstream issue for the Backlog.md project (both bugs: the destructive rewrite on ENAMETOOLONG, and the raw-tail drop on rewrite) - the draft lives in this task; the maintainer reviews and submits it (outward-facing action, maintainer's call).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
EXECUTION (2026-10-10, worker on branch jf853; all edits by hand, the backlog MCP untouched):

PART 1, per-file verdicts (harness copied to /tmp/jf853_ref per the spec; probes always ran with REAL filenames: a renamed copy makes the CLI answer "Task not found" and the probe false-passes, caught and corrected during execution):
- jf-637: pathology = the SECTION:NOTES:ORCHESTRATOR-SIMPLIFY:BEGIN at old line 75, never closed (stack-parse ran it to EOF), plus a raw dated Implementation Notes section (old lines 54-73). Normalization: ONE canonical SECTION:NOTES region now wraps the raw notes (dated heading kept verbatim INSIDE the region, the jf-492 fold precedent), and the orchestrator-simplify block is folded in behind its own provenance line; the unclosed marker is dissolved. Why dissolved rather than closed in place: the CLI drops an unknown closed marker region on rewrite (the Description's jf-637 0->5 case is exactly this drop; mechanism in the BATTERY notes), so folding the content into the managed notes region is the durable form.
- jf-643: pathology = stray SECTION:NOTES:END at old line 92 (no BEGIN), unclosed SECTION:NOTES:SIMPLIFY-ROUND region at 94-101, and a NESTED second SECTION:NOTES pair (105-107) inside it (the stray nested BEGIN). Normalization: ONE managed Implementation Notes region; the raw first block, the simplify-round content, and the nested region's LIVE VERIFICATION line merged in document order with three provenance lines (one per source region); the stray END, the SIMPLIFY-ROUND BEGIN, and the duplicate scaffold heading dissolved.
- jf-781: pathology = legacy `<!-- NOTES:` pair (old lines 79/126) the canonical extractor does not recognize, plus a raw CLOSED tail and an unmarked/unnumbered DoD. Normalization: the legacy pair converted to canonical SECTION:NOTES form, Description wrapped in SECTION:DESCRIPTION markers, DoD wrapped in DOD markers with the CLI's own #1/#2/#3 numbering (its rewrite renumbers unnumbered checklists anyway; pre-numbering is what makes the roundtrip lossless), CLOSED tail folded in. Side note: the next real MCP edit will also RENAME this file (title starts with "JF-781 - ", regenerated slug 185 bytes, still under the limit).

BATTERY (rt_probe method: scratch backlog project, `backlog task edit <id> --add-label`, pre/post body-content multiset diff; backlog CLI v1.44.0): HEAD baselines reproduced first (jf-637 0, jf-643 0, jf-781 3: the three unnumbered DoD first lines). After normalize+fold: single-edit loss 0/0/0; double-edit (two sequential labels, the second running on the CLI's own first-edit output) 0/0/0. Conservation audit vs HEAD: jf-637/jf-643 zero content lines lost; jf-781 only the three DoD first lines, replaced by their #N-renumbered forms. Census (fold_sweep verify): 0/3 residual orphans. Mechanism notes recorded for the future: the CLI passes an already-canonical body through byte-identical (jf-637/643 path) and fully re-serializes when a section is not in its expected shape (jf-781 path); the re-serialization wraps unmarked Description/DoD, renumbers checklists, converts nothing else, and drops anything outside ITS recognized SECTION:NOTES:BEGIN-to-first-END span.

PART 2: guard notes landed at the top of the managed Description section of jf-724 (already had one; note inserted under SECTION:DESCRIPTION:BEGIN) and of jf-771/jf-774/jf-782 (empty Description headings; a canonical SECTION:DESCRIPTION wrapper created for the note; wording kept to tripwire plus pointer, the mechanism lives once here and in the draft). Both upstream bugs were re-reproduced live today before drafting; the observed behaviors are exactly as the draft's repro steps describe.

FOLLOW-UPS RECOMMENDED (not in this task's file scope; for the orchestrator): (a) a mechanical guard beats these advisory notes, since the CLI never reads them before deleting: a validator that computes every task file's regenerated slug length (the rt_battery filename check is the seed) plus a hook or blocklist naming the affected files; (b) the roundtrip harness (rt_probe.py/rt_battery.py, this session's copy at /tmp/jf853_ref) needs a durable home or it dies with /tmp, and JF-854's census-to-repo-tool lane is the natural place to carry it.

UPSTREAM ISSUE DRAFT (maintainer reviews and submits; not filed by the worker):

```markdown
Title: `backlog task edit` silently drops file content outside managed markers; a long-title edit fails with ENAMETOOLONG after already deleting the task file

## Environment

- Backlog.md CLI v1.44.0
- Linux (ext4)
- A plain git-backed backlog project (no custom config beyond defaults)

## Bug 1: `backlog task edit` silently deletes content outside the managed marker blocks

Steps to reproduce:

1. Create a scratch project and a task file whose body carries text OUTSIDE the
   managed marker blocks. Give the file a normal generated body (frontmatter
   with id JF-998, then a Description section whose one content line is
   "Managed description content.", wrapped in the standard marker comments
   `<!-- SECTION:DESCRIPTION:BEGIN -->` and `<!-- SECTION:DESCRIPTION:END -->`
   on their own lines exactly as the CLI itself writes them), then append two
   extra lines after the managed section:

       RAW TAIL LINE A: this line sits outside every managed marker block.

       RAW TAIL LINE B: so does this one.

2. Run any metadata edit, e.g.:

       backlog task edit JF-998 --add-label somelabel

3. The command exits 0 and reports success. The two RAW TAIL lines are gone
   from the file; only the managed sections remain.

Expected: either the unmanaged lines are preserved verbatim, or the command
refuses to edit a file with unmanaged content. Silent data loss on a metadata
edit is the worst outcome because nothing prompts a backup.

## Bug 2: a task whose title slugifies past the 255-byte filename limit loses the file entirely

Steps to reproduce:

1. Create a task file (id JF-999) with a normal-length filename but a long
   title (about 300 characters), or simply keep a task whose title grew over
   time while its
   filename stayed shorter (this is how we hit it: the stored filename was 109
   bytes; the filename the CLI regenerates from the title is 337 bytes).

2. Run any edit on it:

       backlog task edit JF-999 --add-label somelabel

3. Observed:

       ENAMETOOLONG: name too long, open '<project>/backlog/tasks/jf-999 - <regenerated slug, 337 bytes>.md'

   The command exits 1, and the ORIGINAL task file has already been removed
   from backlog/tasks/ at that point. The task and all of its content are gone
   unless recovered from git.

Expected: the edit should fail BEFORE removing the original file (validate the
regenerated filename first, or write-then-rename), and ideally a title that
cannot slugify into a valid filename should be rejected at creation time with
an actionable error.

## Suspected sites (from reading the source, not a debugged root cause)

- Bug 1: the edit path rebuilds the task body from the parsed managed sections;
  `updateChecklistContent` and the section rewrite helpers appear to swallow
  anything they do not recognize instead of carrying it through.
- Bug 2: the rename appears to happen before the new file is opened for write,
  so the ENAMETOOLONG on open leaves the directory without either file.

Both were found while mechanically folding unmanaged historical content into
managed sections across a large backlog; happy to provide more traces if
useful.
```
<!-- SECTION:NOTES:END -->

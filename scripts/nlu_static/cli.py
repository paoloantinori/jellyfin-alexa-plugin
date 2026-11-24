#!/usr/bin/env python3
"""Command line entry point for the nlu_static toolkit (JF-954).

Run from the repository root as::

    python3 scripts/nlu_static/cli.py <subcommand> ...

Each subcommand registers one function (``_register_snapshot`` and so on)
so later JF-954 subtasks can add their own without merge conflicts. The
loaders return data; the CLI does the printing. Every artifact this CLI
writes is deterministic: sorted keys, sorted lists, no timestamps, no
absolute paths.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

_SCRIPTS_DIR = Path(__file__).resolve().parents[1]
if str(_SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS_DIR))

from nlu_static import fixture_vocab, ledger, matcher, producer_jellyfin, token_collisions, triage as nlu_triage  # noqa: E402
from nlu_static import graph as nlu_graph  # noqa: E402
from nlu_static.effective_model import (  # noqa: E402
    EffectiveModel,
    build_effective_model,
    load_snapshot,
    unwrap_language_model,
)
from nlu_static.normalize import skip_line  # noqa: E402


def _print_json(payload: dict | list, out: str | None) -> None:
    """Write a deterministic JSON document to stdout or the --out file."""
    text = json.dumps(payload, indent=2, sort_keys=True) + "\n"
    if out:
        Path(out).write_text(text, encoding="utf-8")
    else:
        sys.stdout.write(text)


def effective_summary(model: EffectiveModel) -> dict:
    """The deterministic per-locale summary: per slot type kind, entry count and source."""
    types: dict[str, dict] = {}
    for vocab in model.types:  # build_effective_model already emits name-sorted
        types[vocab.name] = {
            "kind": vocab.kind,
            "entry_count": len(vocab.entries),
            "source": "snapshot" if vocab.name in model.snapshot_types else "committed",
        }
    return {
        "locale": model.locale,
        "source": model.source,
        "notes": list(model.notes),
        "types": types,
    }


_SKIP_CONTINUE = object()


def _skip_locale(locale: str, locale_arg_given: bool) -> int | object:
    """Handle the mandated SKIP line for one locale; three outcomes.

    Returns the refusal exit code when an EXPLICITLY named locale skips
    (a typo'd locale must never read as all-clean, the fixtures
    subcommand's convention), the ``_SKIP_CONTINUE`` sentinel when an
    unnamed locale in an --all sweep skips (the caller continues its
    loop), and None when the locale can be processed. The SKIP line
    always rides stderr (diagnostics; stdout stays for artifacts).
    """
    line = skip_line(locale)
    if line is None:
        return None
    # The SKIP line itself is the refusal message for the named case: the
    # bare exit code avoids a doubled "error: SKIP ..." line.
    print(line, file=sys.stderr)
    return 2 if locale_arg_given else _SKIP_CONTINUE


def _fail(err: Exception) -> int:
    """Print one error line for a refused input and return the refusal exit code."""
    print(f"error: {err}", file=sys.stderr)
    return 2


def cmd_snapshot(args: argparse.Namespace) -> int:
    """Fetch item names from a Jellyfin server and write a catalog snapshot."""
    try:
        url, api_key = producer_jellyfin.resolve_connection(args.url, args.api_key)
    except ValueError as err:
        return _fail(err)
    try:
        item_types = tuple(args.types) if args.types else None
        snapshot = producer_jellyfin.build_snapshot(url, api_key, item_types=item_types)
    except Exception as err:  # the HTTP layer raises many shapes; redact them all
        return _fail(ValueError(producer_jellyfin.redact(str(err), api_key)))
    _print_json(snapshot, args.out)
    return 0


def _load_optional_snapshot(args: argparse.Namespace) -> tuple[dict | None, int | None]:
    """Load the ``--snapshot`` file when the subcommand carries one.

    Returns (snapshot, None) on success and (None, refusal exit code) on a
    refused file, with the error already printed; every snapshot-taking
    subcommand shares this one loader.
    """
    path = getattr(args, "snapshot", None)
    if not path:
        return None, None
    try:
        return load_snapshot(path), None
    except (OSError, ValueError) as err:
        return None, _fail(err)


def cmd_effective(args: argparse.Namespace) -> int:
    """Print the deterministic effective-model summary for one locale."""
    snapshot, refusal = _load_optional_snapshot(args)
    if refusal is not None:
        return refusal
    try:
        model = build_effective_model(args.locale, snapshot=snapshot)
    except (OSError, ValueError) as err:
        return _fail(err)
    _print_json(effective_summary(model), args.out)
    return 0


def _register_snapshot(subparsers: argparse._SubParsersAction) -> None:
    """Register the snapshot subcommand (JF-954 subtask 8 adds --from-plugin here)."""
    parser = subparsers.add_parser(
        "snapshot",
        help="fetch item names from Jellyfin and write a catalog snapshot",
    )
    parser.add_argument("--url", help="Jellyfin base URL (default: env JELLYFIN_URL)")
    parser.add_argument(
        "--api-key",
        help="Jellyfin API key (default: env JELLYFIN_API_KEY); sent only as the ApiKey query parameter and never printed",
    )
    parser.add_argument(
        "--types",
        nargs="+",
        metavar="ITEM_TYPE",
        choices=sorted(producer_jellyfin.ITEM_TYPE_TO_SLOT_TYPE),
        help="subset of Jellyfin item types to fetch (default: all of Series, MusicArtist, MusicAlbum, Movie, AudioBook, Audio)",
    )
    parser.add_argument("--out", help="write the snapshot JSON here instead of stdout")
    parser.set_defaults(func=cmd_snapshot)


def _register_effective(subparsers: argparse._SubParsersAction) -> None:
    """Register the effective subcommand."""
    parser = subparsers.add_parser(
        "effective",
        help="print a deterministic effective-model summary for a locale",
    )
    parser.add_argument("--locale", required=True, help="Alexa locale, for example es-ES")
    parser.add_argument(
        "--snapshot",
        help="catalog snapshot JSON file to overlay on the committed model",
    )
    parser.add_argument("--out", help="write the summary JSON here instead of stdout")
    parser.set_defaults(func=cmd_effective)


def cmd_tokens(args: argparse.Namespace) -> int:
    """Run the token-ownership collision lint over one locale or all locales."""
    snapshot, refusal = _load_optional_snapshot(args)
    if refusal is not None:
        return refusal
    if args.update_baseline and snapshot is not None:
        # The baseline tracks the committed models only; letting snapshot
        # entries in would freeze one user's library names into the repo.
        return _fail(
            ValueError(
                "--update-baseline and --snapshot are mutually exclusive: "
                "the baseline tracks the committed models only"
            )
        )
    if args.locale:
        locales = [args.locale]
    else:
        locales = token_collisions.committed_locales()
    try:
        reports = [token_collisions.analyze_locale(locale, snapshot=snapshot) for locale in locales]
    except (OSError, ValueError) as err:
        return _fail(err)
    if args.update_baseline:
        for report in reports:
            if report.skip_reason is not None:
                print(report.skip_reason)
        try:
            candidates = token_collisions.baseline_candidates(reports)
            added, removed = token_collisions.update_baseline(
                token_collisions.default_baseline_path(), candidates, locales=locales
            )
        except (OSError, ValueError) as err:
            return _fail(err)
        print(
            f"baseline updated: {token_collisions.default_baseline_path().name} "
            f"({len(candidates)} identities, added {added}, removed {removed})"
        )
        return 0
    try:
        baseline = token_collisions.load_baseline(token_collisions.default_baseline_path())
    except (OSError, ValueError) as err:
        return _fail(err)
    known = set(baseline)
    print(f"token collisions over {len(locales)} locale(s)")
    non_baselined_count = 0
    for report in reports:
        if report.skip_reason is not None:
            # Scoped to the token-dependent kinds; the catalog kind below
            # still runs (its comparison needs no word tokens).
            print(report.skip_reason)
        for note in report.notes:
            print(f"  note [{report.locale}]: {note}")
        non, _ = token_collisions.split_baselined(report.findings, baseline)
        non_baselined_count += len(non)
        shown = report.findings if args.all else non
        for finding in shown:
            suffix = ""
            if args.all:
                suffix = f" [baselined: {'true' if finding.identity() in known else 'false'}]"
            print(f"  {finding.identity()}{suffix}")
            if finding.detail:
                print(f"      {finding.detail}")
        if report.catalog_findings or report.catalog_counts:
            counts = " ".join(f"{name}={count}" for name, count in report.catalog_counts)
            print(f"  CATALOG_VS_MODEL {report.locale}: {counts}")
            for finding in report.catalog_findings:
                print(f"    {finding.identity()}")
                if finding.detail:
                    print(f"        {finding.detail}")
            if report.catalog_truncated:
                print(f"    ... {report.catalog_truncated} more not shown")
            # Catalog findings are never baselined, so every one of them is
            # non-baselined; the summary must not contradict the printed
            # section above it (shown list plus the withheld tail).
            non_baselined_count += len(report.catalog_findings) + report.catalog_truncated
    stale = token_collisions.stale_identities(reports, baseline)
    print(f"non-baselined findings: {non_baselined_count}")
    print(f"stale baseline identities: {len(stale)}")
    for identity in stale:
        print(f"  {identity}")
    return 0


def _register_tokens(subparsers: argparse._SubParsersAction) -> None:
    """Register the tokens subcommand (JF-954.2)."""
    parser = subparsers.add_parser(
        "tokens",
        help="lint carrier words that also own a slot value in another intent",
    )
    parser.add_argument(
        "--locale",
        help="single Alexa locale to lint (default: every committed locale)",
    )
    parser.add_argument(
        "--snapshot",
        help="catalog snapshot JSON file; enables the CATALOG_VS_MODEL kind",
    )
    parser.add_argument(
        "--all",
        action="store_true",
        help="also print baselined findings, each with a baselined: true/false field",
    )
    parser.add_argument(
        "--update-baseline",
        action="store_true",
        help="rewrite the baseline from the current committed models",
    )
    parser.set_defaults(func=cmd_tokens)


def cmd_fixtures(args: argparse.Namespace) -> int:
    """Run the fixture-versus-vocabulary check over one locale or all locales."""
    snapshot, refusal = _load_optional_snapshot(args)
    if refusal is not None:
        return refusal
    locales = [args.locale] if args.locale else fixture_vocab.committed_locales()
    results: list[fixture_vocab.FixtureVocabResult] = []
    checked = 0
    for locale in locales:
        try:
            locale_results, skip, unknown = fixture_vocab.analyze_locale(
                locale,
                snapshot=snapshot,
                models_dir=fixture_vocab.default_models_dir(),
                fixtures_dir=fixture_vocab.default_fixtures_dir(),
            )
        except (OSError, ValueError) as err:
            return _fail(err)
        if skip is not None:
            # Diagnostics go to stderr so --format json keeps stdout parseable.
            print(skip, file=sys.stderr)
            if args.locale:
                # An EXPLICITLY named locale that skips (unknown or typo'd)
                # must fail the command: exiting 0 with '[]' would read as
                # all-clean in a scripted consumer (review round; the
                # effective and tokens subcommands already exit 2 here).
                return _fail(ValueError(skip))
            continue
        if unknown:
            print(
                f"  note [{locale}]: UNKNOWN_BUILTIN slot type(s) treated as free-text: "
                f"{', '.join(unknown)} (the classify table needs these)",
                file=sys.stderr,
            )
        results.extend(locale_results)
        checked += 1
    results.sort(key=lambda result: result.sort_key())
    if args.format == "json":
        payload = [result.payload() for result in results]
        _print_json(payload, args.out)
        return 0
    counts: dict[str, int] = {}
    for result in results:
        counts[result.status] = counts.get(result.status, 0) + 1
    summary = " ".join(f"{status}={counts[status]}" for status in sorted(counts))
    print(f"fixture-vocab check over {checked} locale(s), {len(results)} slot expectation(s): {summary}")
    for result in results:
        if not fixture_vocab.is_reportable(result):
            continue
        kind = f"/{result.type_kind}" if result.type_kind else ""
        type_field = f"{result.type}{kind}" if result.type else "no type"
        print(
            f"  [{result.locale}] {result.status} slot '{result.slot}' type={type_field} "
            f"intent={result.expected_intent} mode={result.mode}"
        )
        print(f"      utterance: {result.utterance}")
    return 0


def _register_fixtures(subparsers: argparse._SubParsersAction) -> None:
    """Register the fixtures subcommand (JF-954.3)."""
    parser = subparsers.add_parser(
        "fixtures",
        help="check NLU fixture slot values against the effective slot type vocabulary",
    )
    parser.add_argument(
        "--locale",
        help="single Alexa locale to check (default: every committed locale)",
    )
    parser.add_argument(
        "--snapshot",
        help="catalog snapshot JSON file; answers the live question (which pinned values the live catalog lacks)",
    )
    parser.add_argument(
        "--format",
        choices=("json", "text"),
        default="json",
        help="output format (default: json, a sorted list of result objects)",
    )
    parser.add_argument("--out", help="write the JSON output here instead of stdout")
    parser.set_defaults(func=cmd_fixtures)


def _ledger_dir_or_fail(args: argparse.Namespace) -> Path | None:
    """Resolve the --dir flag; None after printing the error.

    Without --dir the default follows the same rule as the writer: the
    NLU_LEDGER_DIR environment variable first, then the committed results
    dir, so a suite run with the override and a stats call without --dir
    read the same location.
    """
    if args.dir:
        directory = Path(args.dir)
    else:
        directory = Path(os.environ.get("NLU_LEDGER_DIR") or ledger.DEFAULT_RESULTS_DIR)
    if not directory.is_dir():
        _fail(FileNotFoundError(f"ledger directory not found: {directory}"))
        return None
    return directory


def _load_sessions_or_fail(args: argparse.Namespace) -> list[dict] | None:
    """Load the ledger sessions from the resolved dir; None after printing the error."""
    directory = _ledger_dir_or_fail(args)
    if directory is None:
        return None
    try:
        return ledger.load_sessions(directory)
    except OSError as err:
        # An unreadable session file is a refusal, not a traceback.
        return _fail(err)


def cmd_ledger_stats(args: argparse.Namespace) -> int:
    """Print the per-row flip statistics over the ledger sessions."""
    sessions = _load_sessions_or_fail(args)
    if sessions is None:
        return 2
    try:
        payload = ledger.compute_stats(sessions, locale=args.locale, last=args.last)
    except ValueError as err:
        # compute_stats owns the --last invariant (positive integer) and the
        # CLI turns its refusal into the shared clean-error exit.
        return _fail(err)
    if args.format == "text":
        sys.stdout.write(ledger.render_stats_text(payload))
    else:
        _print_json(payload, None)
    return 0


def cmd_ledger_list(args: argparse.Namespace) -> int:
    """Print the ledger sessions with row counts, completeness and rebuilt flags."""
    sessions = _load_sessions_or_fail(args)
    if sessions is None:
        return 2
    payloads = [ledger.session_payload(session) for session in sessions]
    sys.stdout.write(ledger.render_sessions_text(payloads))
    return 0


def _register_ledger(subparsers: argparse._SubParsersAction) -> None:
    """Register the ledger subcommand (JF-954.5) with its stats and list children."""
    parser = subparsers.add_parser(
        "ledger",
        help="statistics over the recorded live NLU suite sessions",
    )
    ledger_sub = parser.add_subparsers(dest="ledger_command", required=True)
    stats = ledger_sub.add_parser(
        "stats",
        help="per-row flip statistics over the ledger sessions",
    )
    stats.add_argument("--dir", help="ledger directory (default: tests/integration/results)")
    stats.add_argument("--locale", help="restrict the row keys to one locale, for example es-ES")
    stats.add_argument("--last", type=int, metavar="N", help="consider only the last N sessions")
    stats.add_argument(
        "--format",
        choices=("json", "text"),
        default="json",
        help="output format (default: json, a deterministic document)",
    )
    stats.set_defaults(func=cmd_ledger_stats)
    listing = ledger_sub.add_parser(
        "list",
        help="list the ledger sessions with completeness and rebuilt flags",
    )
    listing.add_argument("--dir", help="ledger directory (default: tests/integration/results)")
    listing.set_defaults(func=cmd_ledger_list)


def _resolve_graph_locales(args: argparse.Namespace) -> list[str]:
    """The locale list a graph/match/classify run covers from --locale/--all."""
    if args.locale:
        return [args.locale]
    return fixture_vocab.committed_locales()


def _build_model_or_fail(locale: str, snapshot: dict | None) -> EffectiveModel | None:
    """build_effective_model with the shared refusal handling; None after the error."""
    try:
        return build_effective_model(locale, snapshot=snapshot)
    except (OSError, ValueError) as err:
        _fail(err)
        return None


def cmd_graph_build(args: argparse.Namespace) -> int:
    """Build the subsumption graph for one locale or all locales."""
    snapshot, refusal = _load_optional_snapshot(args)
    if refusal is not None:
        return refusal
    locales = _resolve_graph_locales(args)
    out_dir: Path | None = Path(args.out) if args.out else None
    if len(locales) > 1 and out_dir is None:
        return _fail(ValueError("--all requires --out DIR to write graph_<locale>.json files"))
    if out_dir is not None:
        out_dir.mkdir(parents=True, exist_ok=True)
    failed_locales: list[str] = []
    for locale in locales:
        refused = _skip_locale(locale, locale_arg_given=bool(args.locale))
        if refused is not None:
            if refused is _SKIP_CONTINUE:
                continue
            return refused
        model = _build_model_or_fail(locale, snapshot)
        if model is None:
            # one unreadable model must not starve the other locales:
            # note it, finish the rest, fail the run at the end
            failed_locales.append(locale)
            continue
        payload = nlu_graph.build_graph(locale, model)
        if out_dir is not None:
            path = out_dir / f"graph_{locale}.json"
            # _print_json owns the determinism contract (indent, sorted
            # keys, trailing newline) for every written artifact.
            _print_json(payload, str(path))
            print(f"wrote {path.name} ({len(payload['edges'])} edges)")
        else:
            _print_json(payload, None)
    if failed_locales:
        return 1
    return 0


def _register_graph_build(sub: argparse.ArgumentParser) -> None:
    locale_group = sub.add_mutually_exclusive_group(required=True)
    locale_group.add_argument("--locale", help="single Alexa locale to build, for example es-ES")
    locale_group.add_argument("--all", action="store_true", help="build every committed locale")
    sub.add_argument("--snapshot", help="catalog snapshot JSON file to overlay on the committed model")
    sub.add_argument("--out", help="directory for graph_<locale>.json (required with --all; single locale prints to stdout without it)")
    sub.set_defaults(func=cmd_graph_build)


def cmd_graph_diff(args: argparse.Namespace) -> int:
    """Print the sorted diff between two graph payloads; --fail-on-new exits 1 on added edges."""
    try:
        old = nlu_graph.load_graph(args.old)
        new = nlu_graph.load_graph(args.new)
    except (OSError, ValueError, json.JSONDecodeError) as err:
        return _fail(err)
    lines = nlu_graph.diff_graphs(old, new)
    for line in lines:
        print(line)
    if args.fail_on_new and any(line.startswith("added ") for line in lines):
        return 1
    return 0


def _register_graph_diff(sub: argparse.ArgumentParser) -> None:
    sub.add_argument("old", help="the old graph_<locale>.json")
    sub.add_argument("new", help="the new graph_<locale>.json")
    sub.add_argument(
        "--fail-on-new",
        action="store_true",
        help="exit 1 when an edge was added",
    )
    sub.set_defaults(func=cmd_graph_diff)


DEFAULT_MODEL_REL_PATH = "Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_{locale}.json"


def _git_show_model(base: str, locale: str) -> dict | None:
    """The old model dict from ``git show <base>:<path>``; None after printing the note."""
    import subprocess

    path = DEFAULT_MODEL_REL_PATH.format(locale=locale)
    result = subprocess.run(
        ["git", "show", f"{base}:{path}"],
        capture_output=True,
        text=True,
        check=False,
        cwd=Path(__file__).resolve().parents[2],
    )
    if result.returncode != 0:
        print(f"  note [{locale}]: not in {base} ({path})", file=sys.stderr)
        return None
    try:
        doc = json.loads(result.stdout)
        if not isinstance(doc, dict):
            raise ValueError("top level must be a JSON object")
    except ValueError as err:
        # A malformed model at the base ref is that locale's problem only:
        # note it and let the other locales still diff (review round,
        # finding 1).
        print(f"  note [{locale}]: unreadable model JSON at {base} ({path}: {err})", file=sys.stderr)
        return None
    # The unwrap rides the ONE three-shape discriminator in
    # effective_model (JF-954.7's git-show path needs the same rule as a
    # library function, not this CLI's private copy).
    try:
        return unwrap_language_model(doc)
    except ValueError as err:
        print(f"  note [{locale}]: unreadable model JSON at {base} ({path}: {err})", file=sys.stderr)
        return None


def cmd_graph_diff_git(args: argparse.Namespace) -> int:
    """Diff the graph of a base git ref against the working tree (the reviewer command)."""
    snapshot, refusal = _load_optional_snapshot(args)
    if refusal is not None:
        return refusal
    locales = _resolve_graph_locales(args)
    added_any = False
    for locale in locales:
        refused = _skip_locale(locale, locale_arg_given=bool(args.locale))
        if refused is not None:
            if refused is _SKIP_CONTINUE:
                continue
            return refused
        old_raw = _git_show_model(args.base, locale)
        if old_raw is None:
            continue
        try:
            old_model = build_effective_model(locale, snapshot=snapshot, raw_model=old_raw)
            new_model = build_effective_model(locale, snapshot=snapshot)
        except (OSError, ValueError, json.JSONDecodeError) as err:
            return _fail(err)
        lines = nlu_graph.diff_graphs(
            nlu_graph.build_graph(locale, old_model),
            nlu_graph.build_graph(locale, new_model),
        )
        if not lines:
            continue
        print(f"== {locale} ==")
        for diff_line in lines:
            print(diff_line)
            if diff_line.startswith("added "):
                added_any = True
    if args.fail_on_new and added_any:
        return 1
    return 0


def _register_graph_diff_git(sub: argparse.ArgumentParser) -> None:
    sub.add_argument("--base", required=True, help="git ref to diff against, for example HEAD")
    sub.add_argument("--locale", help="single Alexa locale, for example es-ES (default: every committed locale)")
    sub.add_argument("--all", action="store_true", help="diff every committed locale")
    sub.add_argument("--snapshot", help="catalog snapshot JSON file to overlay on both models")
    sub.add_argument(
        "--fail-on-new",
        action="store_true",
        help="exit 1 when an edge was added in any locale",
    )
    sub.set_defaults(func=cmd_graph_diff_git)


def _register_graph(subparsers: argparse._SubParsersAction) -> None:
    """Register the graph subcommand (JF-954.4) with its build/diff/diff-git children."""
    parser = subparsers.add_parser(
        "graph",
        help="sample-subsumption graph: which intents can swallow another intent's phrasing",
    )
    graph_sub = parser.add_subparsers(dest="graph_command", required=True)
    build = graph_sub.add_parser("build", help="build graph_<locale>.json for one locale or all")
    _register_graph_build(build)
    diff = graph_sub.add_parser("diff", help="diff two graph payloads (added/removed/changed edges)")
    _register_graph_diff(diff)
    diff_git = graph_sub.add_parser(
        "diff-git", help="diff the graph of a git base ref against the working tree"
    )
    _register_graph_diff_git(diff_git)


def cmd_match(args: argparse.Namespace) -> int:
    """Print the intents with a sample reading that covers one real utterance."""
    snapshot, refusal = _load_optional_snapshot(args)
    if refusal is not None:
        return refusal
    refused = _skip_locale(args.locale, locale_arg_given=True)
    if refused is not None:
        return refused
    model = _build_model_or_fail(args.locale, snapshot)
    if model is None:
        return 2
    hits = matcher.match_intents(args.utterance, model)
    _print_json(hits, None)
    return 0


def _register_match(subparsers: argparse._SubParsersAction) -> None:
    """Register the match subcommand (JF-954.4)."""
    parser = subparsers.add_parser(
        "match", help="match a real utterance against every sample pattern of a locale"
    )
    parser.add_argument("--locale", required=True, help="Alexa locale, for example es-ES")
    parser.add_argument("utterance", help="the real utterance to match")
    parser.add_argument("--snapshot", help="catalog snapshot JSON file to overlay on the committed model")
    parser.set_defaults(func=cmd_match)


def cmd_classify_fixtures(args: argparse.Namespace) -> int:
    """Classify every fixture row of one locale or all locales (the triage input)."""
    snapshot, refusal = _load_optional_snapshot(args)
    if refusal is not None:
        return refusal
    locales = _resolve_graph_locales(args)
    results: list[dict] = []
    for locale in locales:
        refused = _skip_locale(locale, locale_arg_given=bool(args.locale))
        if refused is not None:
            if refused is _SKIP_CONTINUE:
                continue
            return refused
        rows, skip_reason = fixture_vocab.load_fixture_rows(
            fixture_vocab.default_fixtures_dir(), locale
        )
        if skip_reason is not None:
            print(skip_reason, file=sys.stderr)
            if args.locale:
                return _fail(ValueError(skip_reason))
            continue
        model = _build_model_or_fail(locale, snapshot)
        if model is None:
            return 2
        prepared = matcher.PreparedMatcher(model)
        malformed = 0
        for row in rows:
            utterance = row.get("utterance")
            expected_intent = row.get("expected_intent")
            if not isinstance(utterance, str) or not isinstance(expected_intent, str):
                # A row without an utterance or an expected intent has
                # nothing to classify; counting and reporting it keeps the
                # skip explicit instead of silent (parent rule; one line
                # per locale, per-row stderr lines would flood --all).
                malformed += 1
                continue
            verdict = matcher.classify_fixture_row(row, model, prepared)
            verdict["locale"] = locale
            verdict["utterance"] = utterance
            verdict["expected_intent"] = expected_intent
            results.append(verdict)
        if malformed:
            print(
                f"  note [{locale}]: {malformed} fixture row(s) without a "
                "string utterance or expected_intent were skipped",
                file=sys.stderr,
            )
    results.sort(
        key=lambda r: (r["locale"], r["utterance"], r["expected_intent"])
    )
    _print_json(results, args.out)
    return 0


def _register_classify_fixtures(subparsers: argparse._SubParsersAction) -> None:
    """Register the classify-fixtures subcommand (JF-954.4; JF-954.6 consumes its JSON)."""
    parser = subparsers.add_parser(
        "classify-fixtures",
        help="classify NLU fixture rows as UNCONTESTED, CONTESTED or NO_SAMPLE_MATCH",
    )
    parser.add_argument("--locale", help="single Alexa locale to classify (default: every committed locale)")
    parser.add_argument("--all", action="store_true", help="classify every committed locale")
    parser.add_argument("--snapshot", help="catalog snapshot JSON file to overlay on the committed model")
    parser.add_argument("--out", help="write the JSON output here instead of stdout")
    parser.set_defaults(func=cmd_classify_fixtures)


def cmd_triage(args: argparse.Namespace) -> int:
    """Classify every red row of one ledger session with the fixed precedence."""
    snapshot, refusal = _load_optional_snapshot(args)
    if refusal is not None:
        return refusal
    sessions = _load_sessions_or_fail(args)
    if sessions is None:
        return 2
    try:
        payload = nlu_triage.triage(
            sessions,
            session_arg=args.session,
            snapshot=snapshot,
            history=args.history,
            flaky_min_flips=args.flaky_min_flips,
        )
    except ValueError as err:
        return _fail(err)
    for line in payload["skip_lines"]:
        print(line, file=sys.stderr)
    if args.format == "json":
        _print_json(payload, args.out)
    else:
        text = nlu_triage.render_markdown(payload)
        if args.out:
            Path(args.out).write_text(text, encoding="utf-8")
        else:
            sys.stdout.write(text)
    if args.fail_on == "systematic" and payload["totals"]["verdicts"][nlu_triage.V_SYSTEMATIC]:
        return 1
    return 0


def _register_triage(subparsers: argparse._SubParsersAction) -> None:
    """Register the triage subcommand (JF-954.6)."""
    parser = subparsers.add_parser(
        "triage",
        help="classify every red NLU row of a ledger session with the fixed precedence",
    )
    parser.add_argument(
        "--session",
        help="session id, or 'latest' to accept an incomplete session (default: the latest complete session)",
    )
    parser.add_argument(
        "--dir", help="ledger directory (default: tests/integration/results)"
    )
    parser.add_argument(
        "--snapshot",
        help="catalog snapshot JSON file; switches the vocabulary rule to the live-catalog question",
    )
    parser.add_argument(
        "--history",
        type=int,
        default=nlu_triage.DEFAULT_HISTORY,
        metavar="N",
        help="how many prior sessions the flaky rule reads (default: %(default)s)",
    )
    parser.add_argument(
        "--flaky-min-flips",
        type=int,
        default=nlu_triage.DEFAULT_FLAKY_MIN_FLIPS,
        metavar="N",
        help="same-model flips that make a row KNOWN_FLAKY (default: %(default)s)",
    )
    parser.add_argument(
        "--format",
        choices=("md", "json"),
        default="md",
        help="output format (default: md)",
    )
    parser.add_argument("--out", help="write the report here instead of stdout")
    parser.add_argument(
        "--fail-on",
        choices=("systematic",),
        help="exit 1 when at least one SYSTEMATIC_CANDIDATE row exists",
    )
    parser.set_defaults(func=cmd_triage)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="nlu_static", description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    _register_snapshot(subparsers)
    _register_effective(subparsers)
    _register_tokens(subparsers)
    _register_fixtures(subparsers)
    _register_ledger(subparsers)
    _register_graph(subparsers)
    _register_match(subparsers)
    _register_classify_fixtures(subparsers)
    _register_triage(subparsers)
    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    sys.exit(main())

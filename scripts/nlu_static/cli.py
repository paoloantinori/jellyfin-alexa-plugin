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
import sys
from pathlib import Path

_SCRIPTS_DIR = Path(__file__).resolve().parents[1]
if str(_SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS_DIR))

from nlu_static import producer_jellyfin, token_collisions  # noqa: E402
from nlu_static.effective_model import EffectiveModel, build_effective_model, load_snapshot  # noqa: E402


def _print_json(payload: dict, out: str | None) -> None:
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


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="nlu_static", description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    _register_snapshot(subparsers)
    _register_effective(subparsers)
    _register_tokens(subparsers)
    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    sys.exit(main())

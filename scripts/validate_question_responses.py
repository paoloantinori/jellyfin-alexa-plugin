#!/usr/bin/env python3
"""Dead-mic question detector (JF-549/JF-550 pattern check).

A response that ASKS the user a question but ships with shouldEndSession=true
(ResponseBuilder.Tell) is spoken with the microphone closed: the user's answer
goes nowhere. Live incident 2026-09-12 17:45 (JF-549): PlayEpisode asked
"Quale serie vorresti guardare?" three times, each time with a closed mic.

This check finds every site with that shape:

1. QUESTION KEYS: locale response strings whose value ends with '?' in at
   least one locale (the last sentence of the string decides; retry
   instructions like "Per favore riprova." are NOT questions and are not
   flagged).
2. DEAD-MIC SITES: `ResponseBuilder.Tell(...)` calls whose speech argument is
   one of those keys - either `ResponseStrings.Get("Key", ...)` inline in the
   call, or a local variable assigned from `ResponseStrings.Get("Key", ...)`
   earlier in the same file.

Advisory by default (exit 0): the output is the worklist for the dead-mic
sweep skill (.claude/skills/dead-mic-sweep). `--strict` exits 1 when any site
is found, for gating. A site may be suppressed with an inline comment on the
Tell line: `// dead-mic-ok: <reason>` (the reason is echoed in the report).

Usage:
  python3 scripts/validate_question_responses.py [--strict]
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCALE_DIR = os.path.join(REPO_ROOT, "Jellyfin.Plugin.AlexaSkill", "Alexa", "Locale")
SRC_GLOB = os.path.join(REPO_ROOT, "Jellyfin.Plugin.AlexaSkill", "**", "*.cs")

SUPPRESSION = re.compile(r"dead-mic-ok:\s*(?P<reason>[^\n]*)")
GET_KEY_INLINE = "ResponseStrings.Get("


def load_question_keys() -> dict[str, tuple[str, str]]:
    """Map every question-shaped response key to (sample_locale, sample_value).

    Question-shaped: at least one SENTENCE of the value ends with '?'. The
    question is often the FIRST sentence with a retry instruction following
    ("Per quanto devo impostare il timer? Per favore indica un numero."), so
    testing only the value's tail would miss exactly the dead-mic sites.
    """

    def any_sentence_questions(value: str) -> bool:
        # Split on sentence-ending punctuation followed by whitespace or end;
        # SSML attributes can contain dots, but a '?' inside an attribute value
        # is still a question mark in the spoken text.
        sentences = re.split(r"(?<=[.!?])\s+", value)
        return any(s.rstrip().endswith("?") for s in sentences)

    keys: dict[str, tuple[str, str]] = {}
    for path in sorted(glob.glob(os.path.join(LOCALE_DIR, "*.json"))):
        locale = os.path.basename(path)[:-5]
        with open(path, encoding="utf-8") as fh:
            for key, value in json.load(fh).items():
                if isinstance(value, str) and any_sentence_questions(value):
                    keys.setdefault(key, (locale, value))
    return keys


def balanced_span(source: str, open_idx: int) -> str:
    """The text from the '(' at open_idx to its matching ')' (strings skipped)."""
    depth = 0
    i = open_idx
    in_string = False
    while i < len(source):
        ch = source[i]
        if in_string:
            if ch == "\\":
                i += 2
                continue
            if ch == '"':
                in_string = False
        elif ch == '"':
            in_string = True
        elif ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return source[open_idx : i + 1]
        i += 1
    return source[open_idx : open_idx + 400]


def line_of(source: str, idx: int) -> int:
    return source.count("\n", 0, idx) + 1


def scan_source(question_keys: dict[str, tuple[str, str]]) -> list[dict]:
    """Find dead-mic Tell sites. Each finding: file, line, key, suppressed reason."""
    findings: list[dict] = []
    tell_re = re.compile(r"ResponseBuilder\s*\.\s*Tell\s*\(")
    var_get_re = re.compile(
        r"(?:var|string)\s+(?P<name>\w+)\s*=\s*ResponseStrings\.Get\(\s*\"(?P<key>[^\"]+)\""
    )

    for path in sorted(glob.glob(SRC_GLOB, recursive=True)):
        rel = os.path.relpath(path, REPO_ROOT)
        if os.sep + "obj" + os.sep in path or os.sep + "bin" + os.sep in path:
            continue
        with open(path, encoding="utf-8") as fh:
            source = fh.read()

        # Local variables holding a question-keyed string, for flow sites.
        question_vars: dict[str, str] = {}
        for m in var_get_re.finditer(source):
            if m.group("key") in question_keys:
                question_vars[m.group("name")] = m.group("key")

        for m in tell_re.finditer(source):
            open_idx = m.end() - 1
            span = balanced_span(source, open_idx)
            # The Tell statement line (for suppression comments on the same line).
            line_start = source.rfind("\n", 0, m.start()) + 1
            line_end = source.find("\n", m.start())
            if line_end == -1:
                line_end = len(source)
            tell_line = source[line_start:line_end]

            key = None
            km = re.search(r"ResponseStrings\.Get\(\s*\"(?P<key>[^\"]+)\"", span)
            if km and km.group("key") in question_keys:
                key = km.group("key")
            if key is None:
                inner = span[1:-1].strip()
                if inner in question_vars:
                    key = question_vars[inner]
            if key is None:
                continue

            suppressed = SUPPRESSION.search(tell_line)
            findings.append(
                {
                    "file": rel,
                    "line": line_of(source, m.start()),
                    "key": key,
                    "sample": question_keys[key][1],
                    "suppressed": suppressed.group("reason").strip() if suppressed else None,
                }
            )
    return findings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--strict", action="store_true", help="exit 1 when unsuppressed sites exist")
    args = parser.parse_args()

    question_keys = load_question_keys()
    findings = scan_source(question_keys)

    active = [f for f in findings if not f["suppressed"]]
    suppressed = [f for f in findings if f["suppressed"]]

    print(f"Question-shaped locale keys: {len(question_keys)}")
    print(f"Tell sites using one: {len(findings)} "
          f"({len(active)} live, {len(suppressed)} suppressed with dead-mic-ok)\n")

    if active:
        print("DEAD-MIC QUESTION SITES (Tell asks a question with the mic closed):")
        for f in active:
            print(f"  {f['file']}:{f['line']}  {f['key']}  \"{f['sample'][:60]}\"")
    else:
        print("No unsuppressed dead-mic question sites.")

    if suppressed:
        print("\nSuppressed (dead-mic-ok):")
        for f in suppressed:
            print(f"  {f['file']}:{f['line']}  {f['key']}  reason: {f['suppressed']}")

    print(
        "\nFix shape: elicit instead of Tell (BuildDialogElicitResponse), register the "
        "intent in dialog.intents in ALL 17 templates (anti-pattern #9), add the "
        "cancel-word hatch for the open elicit. Exemplar: JF-549 "
        "(PlayEpisodeIntentHandler). Sweep driver: JF-550."
    )

    if args.strict and active:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

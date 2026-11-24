"""The sample-subsumption graph: which intents can swallow another intent's phrasing (JF-954.4).

Nodes are intents of one locale. For every ordered pair (A, B) of
different intents, the edge A to B exists when at least one sample pair
qualifies: the two skeletons are IDENTICAL, or pattern(a) matches
witness(b), meaning everything sample b can generate is also a valid
reading of sample a. The edge is the deterministic, diffable artifact:
``graph diff-git`` against a base ref shows the competition a template
change introduces. It does NOT predict the trainer's winner.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

from nlu_static import patterns
from nlu_static.effective_model import EffectiveModel

GRAPH_SCHEMA = 1
MAX_EXAMPLES = 3


@dataclass
class _IntentIndex:
    """One intent's unique skeletons and the buckets the pair scan needs."""

    name: str
    samples: tuple[str, ...]
    unique: list = field(default_factory=list)  # CompiledPattern, one per skeleton
    samples_by_skeleton: dict = field(default_factory=dict)  # skeleton -> sample texts
    by_first_lit: dict = field(default_factory=dict)  # literal -> skeletons opening with it
    by_last_lit: dict = field(default_factory=dict)  # literal -> skeletons closing with it
    closers: list = field(default_factory=list)  # skeletons closing with a slot or empty
    openers: list = field(default_factory=list)  # skeletons opening with a slot


def _intent_index(name: str, patterns_of_intent: list) -> _IntentIndex:
    """Deduplicate identical skeletons within the intent, then bucket by opener.

    Identical skeletons (slots compared by class and, for CLOSED, by
    type) match identically, so they are compiled once and their sample
    texts ride along in ``samples_by_skeleton``.
    """
    index = _IntentIndex(name=name, samples=tuple(c.sample for c in patterns_of_intent))
    for compiled in patterns_of_intent:
        if compiled.skeleton not in index.samples_by_skeleton:
            index.samples_by_skeleton[compiled.skeleton] = [compiled.sample]
            index.unique.append(compiled)
            first = compiled.first_item
            if first is not None and first[0] == "lit":
                index.by_first_lit.setdefault(first[1], []).append(compiled)
            else:
                index.openers.append(compiled)
            last = compiled.last_item
            if last is not None and last[0] == "lit":
                index.by_last_lit.setdefault(last[1], []).append(compiled)
            else:
                index.closers.append(compiled)
        else:
            index.samples_by_skeleton[compiled.skeleton].append(compiled.sample)
    return index


def _candidates_against(b_compiled, a_index: _IntentIndex) -> list:
    """The A skeletons the early-skip lets the witness of ``b_compiled`` try.

    Mirror of the original per-A pre-filter, seen from B: when b opens
    with a literal token, only A skeletons opening with the same literal
    can match the first item, plus every opener skeleton of A; when b
    closes with a literal, only A skeletons closing with the same
    literal can match the last item, plus every closer skeleton of A.
    """
    first = b_compiled.first_item
    if first is not None and first[0] == "lit":
        candidates = list(a_index.by_first_lit.get(first[1], [])) + a_index.openers
    else:
        candidates = list(a_index.unique)
    last = b_compiled.last_item
    if last is not None and last[0] == "lit":
        allowed = set(id(c) for c in a_index.by_last_lit.get(last[1], []))
        allowed.update(id(c) for c in a_index.closers)
        candidates = [c for c in candidates if id(c) in allowed]
    return candidates


def build_graph(locale: str, model: EffectiveModel) -> dict:
    """Build the deterministic graph payload for one locale.

    Pure function of (locale, model): no timestamps, no absolute paths,
    sorted output everywhere, byte-identical across runs over the same
    inputs.
    """
    compiled, unclassified = patterns.compile_model_patterns(model)
    entry_tokens = patterns.closed_entry_tokens(model)
    per_intent: dict[str, list] = {}
    for c in compiled:
        per_intent.setdefault(c.intent, []).append(c)
    indexes = [_intent_index(name, per_intent[name]) for name in sorted(per_intent)]

    edges: list[dict] = []
    for a_index in indexes:
        for b_index in indexes:
            if a_index.name == b_index.name:
                continue
            matched_b_skeletons: set = set()
            identical = False
            matching_a_samples: set[str] = set()
            for b_compiled in b_index.unique:
                # one shared DP memo per B witness: every A skeleton of
                # this intent pair reuses it instead of rebuilding the
                # table from scratch (review round, finding 4)
                witness_memo: dict[tuple[int, int, int], bool] = {}
                b_matched = False
                for a_compiled in _candidates_against(b_compiled, a_index):
                    if patterns.skeleton_equal(a_compiled, b_compiled):
                        identical = True
                        b_matched = True
                        matching_a_samples.add(a_compiled.sample)
                    elif patterns.matches(
                        a_compiled, b_compiled.witness, entry_tokens, memo=witness_memo
                    ):
                        b_matched = True
                        matching_a_samples.add(a_compiled.sample)
                if b_matched:
                    matched_b_skeletons.add(b_compiled.skeleton)
            if not matched_b_skeletons:
                continue
            matched_b_samples: set[str] = set()
            for skeleton in matched_b_skeletons:
                matched_b_samples.update(b_index.samples_by_skeleton[skeleton])
            b_texts = sorted(matched_b_samples)
            examples = _examples(sorted(matching_a_samples), b_texts)
            edges.append({
                "from": a_index.name,
                "to": b_index.name,
                "kind": "IDENTICAL" if identical else "SWALLOW",
                "swallowed": len(matched_b_samples),
                "total": len(b_index.samples),
                "examples": examples,
            })

    edges.sort(key=lambda edge: (edge["from"], edge["to"]))
    universal: list[str] = []
    other_count = len(indexes) - 1
    if other_count > 0:
        swallowers: dict[str, int] = {}
        for edge in edges:
            swallowers[edge["from"]] = swallowers.get(edge["from"], 0) + 1
        for name in sorted(swallowers):
            if swallowers[name] * 2 > other_count:
                universal.append(name)
    return {
        "schema": GRAPH_SCHEMA,
        "locale": locale,
        "mode": model.source,
        "unclassified_types": sorted(unclassified),
        "universal": universal,
        "edges": edges,
    }


def _examples(a_texts: list[str], b_texts: list[str]) -> list[list[str]]:
    """The first MAX_EXAMPLES pairs of the sorted sample-text product.

    The matching relation is skeleton-level, so every (a_text of a
    matching A skeleton, b_text of a matching B skeleton) combination
    qualifies; the sorted product's first three come from the smallest
    texts in order.
    """
    examples: list[list[str]] = []
    for a_text in a_texts:
        for b_text in b_texts:
            examples.append([a_text, b_text])
            if len(examples) >= MAX_EXAMPLES:
                return examples
    return examples


def diff_graphs(old: dict, new: dict) -> list[str]:
    """The deterministic textual diff between two graph payloads.

    Added edges, removed edges and edges whose ``kind`` or ``swallowed``
    changed, one line each, sorted; an empty list means no changes.
    """
    old_edges = {(e["from"], e["to"]): e for e in old.get("edges", [])}
    new_edges = {(e["from"], e["to"]): e for e in new.get("edges", [])}
    lines: list[str] = []
    for key in sorted(set(new_edges) - set(old_edges)):
        e = new_edges[key]
        lines.append(f"added {e['from']} -> {e['to']} kind={e['kind']} swallowed={e['swallowed']}/{e['total']}")
    for key in sorted(set(old_edges) - set(new_edges)):
        e = old_edges[key]
        lines.append(f"removed {e['from']} -> {e['to']} kind={e['kind']} swallowed={e['swallowed']}/{e['total']}")
    for key in sorted(set(old_edges) & set(new_edges)):
        old_e, new_e = old_edges[key], new_edges[key]
        if (
            old_e["kind"] != new_e["kind"]
            or old_e["swallowed"] != new_e["swallowed"]
            or old_e["total"] != new_e["total"]
        ):
            lines.append(
                f"changed {key[0]} -> {key[1]} "
                f"kind={old_e['kind']}->{new_e['kind']} "
                f"swallowed={old_e['swallowed']}/{old_e['total']}"
                f"->{new_e['swallowed']}/{new_e['total']}"
            )
    return lines


def load_graph(path: str | Path) -> dict:
    """Load and minimally validate one graph payload file."""
    graph_path = Path(path)
    doc = json.loads(graph_path.read_text(encoding="utf-8"))
    if not isinstance(doc, dict) or doc.get("schema") != GRAPH_SCHEMA:
        raise ValueError(f"{graph_path}: not a schema {GRAPH_SCHEMA} graph payload")
    if not isinstance(doc.get("edges"), list):
        raise ValueError(f"{graph_path}: 'edges' must be a list")
    return doc

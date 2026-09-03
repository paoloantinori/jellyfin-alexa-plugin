#!/usr/bin/env python3
"""Parse Mermaid flowchart definitions into D3-compatible graph JSON."""

import json
import re
import glob
import os
import html

LOCALES = [
    'ar-SA','de-DE','en-AU','en-CA','en-GB','en-IN','en-US',
    'es-ES','es-MX','es-US','fr-CA','fr-FR','hi-IN','it-IT',
    'ja-JP','nl-NL','pt-BR'
]

def parse_mermaid(text, warnings=None):
    """Parse a Mermaid graph definition into nodes and edges.

    `warnings`, when a list, collects edge endpoints that fail to resolve and
    are not curly-brace node refs (the known {diamond} limitation below; a
    curly brace INSIDE a square-bracket label still warns, which is how the
    JF-462 stray-bracket typo class is caught). An unresolved non-curly
    endpoint means the md line carries syntax this parser would drop.
    """
    nodes = {}
    edges = []

    for line in text.strip().split('\n'):
        line = line.strip()
        if not line or line.startswith('graph ') or line.startswith('flowchart '):
            continue

        # Parse style lines: style NodeId fill:#color,color:#color
        style_match = re.match(r'^style\s+(\S+)\s+(.*)', line)
        if style_match:
            node_id = style_match.group(1)
            style_str = style_match.group(2)
            fill = color = None
            fill_m = re.search(r'fill:(#[0-9a-fA-F]+)', style_str)
            color_m = re.search(r'color:(#[0-9a-fA-F]+)', style_str)
            if fill_m:
                fill = fill_m.group(1)
            if color_m:
                color = color_m.group(1)
            if node_id in nodes:
                if fill:
                    nodes[node_id]['fill'] = fill
                if color:
                    nodes[node_id]['textColor'] = color
            continue

        # Parse edges
        # Patterns: A --> B, A -->|"label"| B, A -.->|"label"| B, A --- B
        # Node defs: NodeId["Label"], NodeId["Label<br/>Detail"]

        edge_patterns = [
            (r'(.+?)\s*-->\|"([^"]*)"\|\s*(.+)', 'solid'),
            (r'(.+?)\s*-\.\->\|"([^"]*)"\|\s*(.+)', 'dashed'),
            (r'(.+?)\s*-->\|([^|]+)\|\s*(.+)', 'solid'),
            (r'(.+?)\s*-\.\->\|([^|]+)\|\s*(.+)', 'dashed'),
            (r'(.+?)\s*-->\s*(.+)', 'solid'),
            (r'(.+?)\s*-\.\->\s*(.+)', 'dashed'),
            (r'(.+?)\s*---\s*(.+)', 'solid'),
        ]

        for pattern, style in edge_patterns:
            m = re.match(pattern, line)
            if m:
                if len(m.groups()) == 3:
                    src_raw, label, tgt_raw = m.groups()
                else:
                    src_raw, tgt_raw = m.groups()
                    label = ''

                src_raw = src_raw.strip()
                tgt_raw = tgt_raw.strip()
                src_id, src_label = parse_node_ref(src_raw)
                tgt_id, tgt_label = parse_node_ref(tgt_raw)

                if warnings is not None:
                    for raw, ref_id in ((src_raw, src_id), (tgt_raw, tgt_id)):
                        # Exempt only true curly refs (NodeId{"label"}); a curly
                        # brace INSIDE a square-bracket label (the Browse lines
                        # carry {browse_category}) must still warn when resolution
                        # fails, or the ec417c59 stray-bracket class stays silent.
                        if ref_id is None and not re.match(r'^\w+\{"', raw):
                            warnings.append(line)

                if src_id:
                    if src_id not in nodes:
                        nodes[src_id] = {'id': src_id, 'label': src_label or src_id}
                    elif src_label and src_label != src_id:
                        nodes[src_id]['label'] = src_label

                if tgt_id:
                    if tgt_id not in nodes:
                        nodes[tgt_id] = {'id': tgt_id, 'label': tgt_label or tgt_id}
                    elif tgt_label and tgt_label != tgt_id:
                        nodes[tgt_id]['label'] = tgt_label

                edges.append({
                    'source': src_id,
                    'target': tgt_id,
                    'label': label.strip(),
                    'style': style,
                })
                break

    # Clean up labels - strip HTML tags for display
    for node in nodes.values():
        label = node.get('label', node['id'])
        label = label.replace('<br/>', '\n').replace('<br>', '\n')
        label = html.unescape(label)
        node['label'] = label

    return list(nodes.values()), edges


def parse_node_ref(text):
    """Parse a node reference like 'NodeId["Label"]' or just 'NodeId'.

    Curly-brace refs (decision diamonds, NodeId{"Label"}) deliberately return
    (None, None): the committed graphs.json carries null targets for those
    edges in search-disambiguation and session-management, and resolving them
    would churn 34 diagrams that are otherwise in sync. Do not "fix" this
    without regenerating and reviewing all 17 locales of both categories.
    """
    # With label: NodeId["Label text"]
    m = re.match(r'^(\w+)\["([^"]*)"\]$', text)
    if m:
        return m.group(1), m.group(2)

    # With label and HTML: NodeId["Label<br/>detail"]
    m = re.match(r'^(\w+)\["(.*)"\]$', text)
    if m:
        return m.group(1), m.group(2)

    # Bare node ID
    m = re.match(r'^(\w+)$', text)
    if m:
        return m.group(1), None

    return None, None


def main():
    diagrams = {}

    for f in sorted(glob.glob('docs/*.md')):
        base = os.path.basename(f).replace('.md', '')

        locale = None
        dtype = None
        for loc in LOCALES:
            if base.endswith('-' + loc):
                dtype = base[:-(len(loc) + 1)]
                locale = loc
                break

        if not locale:
            continue

        with open(f) as fh:
            content = fh.read()

        m = re.search(r'```mermaid\n(.*?)```', content, re.DOTALL)
        if not m:
            continue

        title_m = re.search(r'^#\s+(.*)', content)
        title = title_m.group(1) if title_m else base

        mermaid_src = m.group(1).strip()
        unresolved = []
        nodes, edges = parse_mermaid(mermaid_src, unresolved)
        for line in unresolved:
            print(f'WARNING {dtype}/{locale}: dropped edge endpoint in: {line}')

        if dtype not in diagrams:
            diagrams[dtype] = {}
        diagrams[dtype][locale] = {
            'title': title,
            'nodes': nodes,
            'edges': edges,
        }

    output = {
        'types': list(diagrams.keys()),
        'locales': sorted(set(loc for d in diagrams.values() for loc in d.keys())),
        'diagrams': diagrams,
    }

    with open('docs-site/graphs.json', 'w') as out:
        json.dump(output, out, ensure_ascii=False, indent=2)

    total = sum(len(v) for v in diagrams.values())
    print(f'Parsed {total} diagrams into graph data')
    for dtype in diagrams:
        counts = [f'{loc}: {len(d["nodes"])} nodes, {len(d["edges"])} edges'
                  for loc, d in list(diagrams[dtype].items())[:3]]
        print(f'  {dtype}: {counts[0]}')


if __name__ == '__main__':
    main()

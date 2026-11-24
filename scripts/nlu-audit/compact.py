import json, glob, re, collections

GLOB = "/home/pantinor/data/repo/personal/jellyfin-alexa-plugin/Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_*.json"
SLOT = re.compile(r"\{([^}]+)\}")

def norm(s): return SLOT.sub("{#}", s.strip().lower())
def toks(s): return tuple("{#}" if t == "{#}" else t for t in norm(s).split())

near_by_locale = {}
exact_by_locale = {}
prefix_pair_counts = {}
prefix_unique = {}  # locale -> list of (pair, examples) not in the shared families
shared_pairs = collections.Counter()

for f in sorted(glob.glob(GLOB)):
    loc = re.search(r"model_(.+)\.json", f).group(1)
    d = json.load(open(f)); m = d.get("interactionModel", d); lm = m["languageModel"]
    exact = collections.defaultdict(set); carriers = collections.defaultdict(lambda: collections.defaultdict(set))
    tsets = collections.defaultdict(set); samp = {}
    for it in lm["intents"]:
        n = it["name"]
        for s in it.get("samples", []):
            s = s.strip()
            if not s: continue
            exact[s.lower()].add((n, s))
            carriers[norm(s)][n].add(s)
            tk = toks(s); tsets[n].add(tk); samp[(n, tk)] = s
    ex = []
    for k, v in sorted(exact.items()):
        if len({i for i, _ in v}) > 1:
            ex.append((k, sorted(v)))
    exact_by_locale[loc] = ex
    nr = []
    for c, pi in sorted(carriers.items()):
        if len(pi) < 2: continue
        al = [s for ss in pi.values() for s in ss]
        if len({s.lower() for s in al}) == 1: continue
        nr.append((c, sorted(pi)))
    near_by_locale[loc] = nr
    names = sorted(tsets); pc = collections.Counter(); fams = collections.defaultdict(list)
    for a in range(len(names)):
        for b in range(a+1, len(names)):
            ia, ib = names[a], names[b]
            for ta in tsets[ia]:
                for tb in tsets[ib]:
                    if len(ta) < len(tb) and tb[:len(ta)] == ta:
                        pc[(ia, ib)] += 1; fams[(ia, ib)].append((samp[(ia, ta)], samp[(ib, tb)]))
                    elif len(tb) < len(ta) and ta[:len(tb)] == tb:
                        pc[(ia, ib)] += 1; fams[(ia, ib)].append((samp[(ib, tb)], samp[(ia, ta)]))
    prefix_pair_counts[loc] = pc
    for pair in pc: shared_pairs[pair] += 1

print("=== EXACT PER LOCALE ===")
for loc, ex in exact_by_locale.items():
    if ex:
        print(f"[{loc}]")
        for k, v in ex:
            print(f"   {' ; '.join(f'[{i}] {s!r}' for i, s in v)}")

print()
print("=== NEAR PER LOCALE (carrier -> intents) ===")
for loc, nr in near_by_locale.items():
    print(f"[{loc}] {len(nr)} families")
    for c, intents in nr:
        print(f"   {c}  ->  {intents}")

print()
print("=== PREFIX PAIRS SHARED BY >=2 LOCALES (pair -> #locales, max pair-count) ===")
for pair, nloc in shared_pairs.most_common():
    if nloc >= 2:
        mx = max(prefix_pair_counts[loc][pair] for loc in prefix_pair_counts if pair in prefix_pair_counts[loc])
        print(f"   {nloc:2d} locales, up to {mx:3d} pairs: {pair[0]} <> {pair[1]}")
print()
print("=== PREFIX PAIRS UNIQUE TO ONE LOCALE ===")
for pair, nloc in shared_pairs.most_common():
    if nloc == 1:
        loc = [l for l in prefix_pair_counts if pair in prefix_pair_counts[l]][0]
        print(f"   [{loc}] {prefix_pair_counts[loc][pair]} pairs: {pair[0]} <> {pair[1]}")

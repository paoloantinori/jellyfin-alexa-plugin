import json, glob, re, collections

GLOB = "/home/pantinor/data/repo/personal/jellyfin-alexa-plugin/Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_*.json"
SLOT = re.compile(r"\{([^}]+)\}")

def norm(s): return SLOT.sub("{#}", s.strip().lower())
def toks(s): return tuple("{#}" if t == "{#}" else t for t in norm(s).split())

for f in sorted(glob.glob(GLOB)):
    loc = re.search(r"model_(.+)\.json", f).group(1)
    d = json.load(open(f)); m = d.get("interactionModel", d); lm = m["languageModel"]
    slot_types = {}  # (intent, slotname) -> type
    exact = collections.defaultdict(set)
    carriers = collections.defaultdict(lambda: collections.defaultdict(set))
    tsets = collections.defaultdict(set); samp = {}
    for it in lm["intents"]:
        n = it["name"]
        for sl in it.get("slots", []):
            slot_types[(n, sl["name"])] = sl["type"]
        for s in it.get("samples", []):
            s = s.strip()
            if not s: continue
            exact[s.lower()].add((n, s))
            carriers[norm(s)][n].add(s)
            tk = toks(s); tsets[n].add(tk); samp[(n, tk)] = s
    print(f"\n########## {loc} ##########")
    print("--- EXACT (same string, 2+ intents) ---")
    for k, v in sorted(exact.items()):
        if len({i for i, _ in v}) > 1:
            for i, s in sorted(v): print(f"  [{i}] \"{s}\"")
    print("--- NEAR (same carrier shape, different slots, 2+ intents) ---")
    for c, pi in sorted(carriers.items()):
        if len(pi) < 2: continue
        al = [s for ss in pi.values() for s in ss]
        if len({s.lower() for s in al}) == 1: continue  # exact already reported
        # annotate each sample with its intent + slot types
        parts = []
        for i in sorted(pi):
            ex = sorted(pi[i])[0]
            st = ", ".join(f"{mm.group(1)}:{slot_types.get((i, mm.group(1)),'?')}" for mm in SLOT.finditer(ex))
            parts.append(f"[{i}] \"{ex}\" ({st})")
        print(f"  CARRIER \"{c}\": " + " | ".join(parts))
    print("--- PREFIX (token-prefix across intents, grouped by intent pair) ---")
    names = sorted(tsets)
    fam = collections.defaultdict(set); famex = {}
    for a in range(len(names)):
        for b in range(a + 1, len(names)):
            ia, ib = names[a], names[b]
            for ta in tsets[ia]:
                for tb in tsets[ib]:
                    if len(ta) < len(tb) and tb[:len(ta)] == ta:
                        key = (ia, ib)
                        fam[key].add((ta, tb))
                        famex.setdefault((key, ta, tb), (samp[(ia, ta)], samp[(ib, tb)]))
                    elif len(tb) < len(ta) and ta[:len(tb)] == tb:
                        key = (ia, ib)
                        fam[key].add((tb, ta))
                        famex.setdefault((key, tb, ta), (samp[(ib, tb)], samp[(ia, ta)]))
    for key in sorted(fam, key=lambda k: -len(fam[k])):
        ia, ib = key
        print(f"  {ia}  <>  {ib}   ({len(fam[key])} prefix pairs)")
        shown = 0
        for ta, tb in sorted(fam[key], key=lambda x: len(x[0])):
            if shown >= 6:
                print(f"    ... +{len(fam[key])-6} more")
                break
            sa, sb = famex[(key, ta, tb)]
            print(f"    A:\"{sa}\"  <  B:\"{sb}\"")
            shown += 1

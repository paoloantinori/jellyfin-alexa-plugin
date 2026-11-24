#!/usr/bin/env python3
"""Static NLU slot-consistency audit over model_*.json files.

Checks per locale:
  1. slot_name -> set of types across ALL intents; flag slot names with >1 type
     (excluding documented intentional divergences).
  2. dead slot declaration: slot declared in intent.slots but no sample references it.
  3. undeclared reference: {slot} referenced in a sample but not in intent.slots.
"""
import json
import glob
import re
import sys
from collections import defaultdict

repo = sys.argv[1]
pattern = re.compile(r"\{([A-Za-z0-9_]+)\}")

# Documented intentional divergences (CLAUDE.md) -> suppress from type-divergence findings.
# Each entry: slot name -> set of types that may legitimately co-occur for that slot.
INTENTIONAL = {
    # Song: AMAZON.MusicRecording everywhere, but SongTitle on PlayLibrarySongIntent (JF-940)
    "song": {"AMAZON.MusicRecording", "SongTitle"},
    # Album: AlbumName in it-IT, AMAZON.MusicRecording elsewhere (JF-332)
    "album": {"AlbumName", "AMAZON.MusicRecording"},
    # Artist: JellyfinArtist in some locales, AMAZON.Musician in others (JF-415)
    "musician": {"JellyfinArtist", "AMAZON.Musician"},
    "artist": {"JellyfinArtist", "AMAZON.Musician"},
}


def intentional(locale, slot, types):
    allowed = INTENTIONAL.get(slot)
    if allowed is None:
        return False
    # Album divergence is it-IT-only per JF-332 (only it-IT declares AlbumName);
    # MusicRecording co-occurrence inside another locale would be a real finding.
    if slot == "album" and "AlbumName" in types and locale != "it-IT":
        return False
    return set(types) <= allowed


def main():
    files = sorted(glob.glob(repo + "/Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_*.json"))
    print(f"files: {len(files)}")
    suppressed = []
    for path in files:
        locale = path[-10:-5]
        with open(path, encoding="utf-8") as fh:
            d = json.load(fh)
        m = d.get("interactionModel", d)
        lm = m["languageModel"]

        slot_types = defaultdict(lambda: defaultdict(list))  # name -> type -> [intents]
        dead = []      # (intent, slot, type)
        undeclared = defaultdict(set)  # (intent) -> {slot names}

        for intent in lm["intents"]:
            iname = intent["name"]
            slots = intent.get("slots") or []
            samples = intent.get("samples") or []
            declared = {s["name"]: s.get("type") for s in slots}
            for sname, stype in declared.items():
                slot_types[sname][stype].append(iname)
            referenced = set()
            for sample in samples:
                for ref in pattern.findall(sample):
                    referenced.add(ref)
                    if ref not in declared:
                        undeclared[iname].add(ref)
            for sname, stype in declared.items():
                if sname not in referenced:
                    dead.append((iname, sname, stype))

        issues = []
        for sname, by_type in sorted(slot_types.items()):
            if len(by_type) > 1:
                detail = "; ".join(
                    f"{t} in {', '.join(sorted(ints))}" for t, ints in sorted(by_type.items())
                )
                if intentional(locale, sname, by_type.keys()):
                    suppressed.append((locale, sname, detail))
                else:
                    issues.append(f"TYPE-DIVERGENCE slot '{sname}': {detail}")
        for iname, sname, stype in dead:
            issues.append(f"DEAD-SLOT intent {iname} declares '{sname}' ({stype}) but no sample references it")
        for iname in sorted(undeclared):
            for sname in sorted(undeclared[iname]):
                issues.append(f"UNDECLARED-REF intent {iname} sample references '{sname}' but it is not in slots[]")

        if issues:
            print(f"\n=== {locale} ({len(issues)} issue(s)) ===")
            for i in issues:
                print(f"  {i}")
        else:
            print(f"--- {locale}: clean")

    print("\n=== suppressed intentional divergences (not flagged) ===")
    for locale, sname, detail in suppressed:
        print(f"  {locale} '{sname}': {detail}")


if __name__ == "__main__":
    main()

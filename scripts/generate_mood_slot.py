#!/usr/bin/env python3
"""JF-356: roll the custom Mood slot type to the hand-maintained locales.

TRANSITION SCOPE (JF-316 milestone 2): a locale that has a YAML template
(templates/<locale>.yaml) owns its Mood block IN THE TEMPLATE, and this
script refuses to write that model (it-IT, en-US, en-GB, en-AU, en-CA,
en-IN today; the list grows as JF-316 milestones land). For a mood-word
change in a templated locale, edit the template and run
scripts/generate_interaction_model.py <locale>. For the hand-maintained
locales below, edit LOCALE_MOODS and run this script. When every locale
is templated this script has no writers left and should be deleted.

For each locale still owned here:
  1. Add (or replace) a `Mood` custom slot type with locale-specific values.
  2. Change PlayMoodMusicIntent's `mood` slot from AMAZON.SearchQuery -> Mood.
  3. Keep only slotted mood samples (drop concrete sample-less utterances like
     "play morning music" that leave the slot empty -> anti-pattern #1).

Per-locale mood values reuse the words already in LocalizedMoodMap (de/es/fr/pt/it)
where available, plus translations for the locales the map doesn't cover
(nl-NL, ja-JP, hi-IN, ar-SA, es-MX, es-US, fr-CA). The English locales use the
MoodGenreMap keys directly and are template-owned since JF-316 milestone 2;
the map's it-IT entries stay (the resolver is locale-agnostic, and it-IT's
mood words resolve through them).

Each entry is a list of (value, [synonyms]) tuples. The handler reads the raw
spoken text (moodSlot.Value), so every value AND every synonym must independently
resolve via MoodGenreMap (English key) or LocalizedMoodMap. That mapping lives
in PlayMoodMusicIntentHandler.cs and is locale-agnostic at lookup time, so any
translated word resolves through tier-2 (localized->English) ONLY if present in
LocalizedMoodMap. Translated words NOT in LocalizedMoodMap fall to tier-5
(raw-mood-as-genre), which works only if the word coincides with a Jellyfin
genre name. Therefore, for locales whose words aren't in LocalizedMoodMap,
we ALSO add the words to LocalizedMoodMap in the handler (separate edit).
"""
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODELS_DIR = os.path.join(REPO, "Jellyfin.Plugin.AlexaSkill", "Alexa", "InteractionModel")
TEMPLATES_DIR = os.path.join(MODELS_DIR, "templates")

# Per-locale mood values: list of (canonical_value, [synonyms]).
# Built from LocalizedMoodMap (de/it/es/fr/pt) + curated translations for the rest.
# English keys (relaxing, chill, upbeat, energetic, focus, romantic, happy, sad,
# party, workout, morning, evening, dinner, sleep) are the resolve targets; the
# en-* locales carry them in their YAML templates, not here.
LOCALE_MOODS = {
    "de-DE": [
        ("entspannend", ["entspannt", "entspannende", "entspannendes"]), ("beruhigend", ["beruhigende", "beruhigendes"]), ("beschwingt", ["beschwingte", "beschwingtes"]),
        ("energisch", ["energische", "energisches"]), ("fokus", []), ("romantisch", ["romantische", "romantisches"]),
        ("fröhlich", ["glücklich", "fröhliche", "fröhliches"]), ("traurig", ["traurige", "trauriges"]), ("feier", ["party", "feierliche", "feierliches"]),
        ("training", ["training"]), ("morgens", ["morgentliche"]), ("abend", ["abends", "abendliche"]),
        ("abendessen", []), ("schlafen", ["einschlafen"]),
    ],
    "es-ES": [
        ("relajante", []), ("relajado", []), ("animada", ["animado"]),
        ("enérgica", ["enérgico"]), ("concentración", []), ("romántica", ["romántico"]),
        ("alegre", ["feliz"]), ("triste", []), ("fiesta", []),
        ("entrenamiento", []), ("matutino", []), ("nocturna", ["nocturno"]),
        ("cena", []), ("dormir", ["sueño"]),
    ],
    "es-MX": "es-ES", "es-US": "es-ES",  # Spanish shared

    "fr-FR": [
        ("détendant", ["détendu", "détendue"]), ("reposant", []), ("dynamique", []),
        ("énergique", []), ("concentration", []), ("romantique", []),
        ("heureuse", ["heureux", "joyeuse", "joyeux"]), ("triste", []),
        ("fête", []), ("entraînement", []), ("matinal", []),
        ("soirée", []), ("dîner", []), ("sommeil", ["dormir"]),
    ],
    "fr-CA": "fr-FR",  # French shared

    "pt-BR": [
        ("relaxada", ["relaxado"]), ("calmo", ["calma"]), ("animada", ["animado"]),
        ("energética", ["energético"]), ("foco", []), ("romântica", ["romântico"]),
        ("alegre", ["feliz"]), ("triste", []), ("festa", []),
        ("treino", ["exercício"]), ("manhã", []), ("noite", ["noturna", "noturno"]),
        ("jantar", []), ("dormir", ["sono"]),
    ],
    "nl-NL": [
        ("ontspannend", []), ("rustgevend", []), ("vrolijk", []),
        ("energiek", []), ("concentratie", ["focus"]), ("romantisch", []),
        ("blij", ["gelukkig"]), ("verdrietig", []), ("feest", []),
        ("workout", ["training"]), ("ochtend", []), ("avond", []),
        ("diner", []), ("slapen", ["slaap"]),
    ],
    "ja-JP": [
        # Katakana/English loanwords dominate Alexa music moods in Japanese.
        ("リラックス", []), ("チル", []), ("アップビート", []),
        ("エネルギッシュ", []), ("集中", []), ("ロマンチック", []),
        ("ハッピー", []), ("サッド", []), ("パーティー", []),
        ("ワークアウト", []), ("モーニング", []), ("イブニング", []),
        ("ディナー", []), ("スリープ", []),
    ],
    "hi-IN": [
        ("आराम", []), ("शांत", []), ("उत्साही", []),
        ("ऊर्जावान", []), ("ध्यान", []), ("रोमांटिक", []),
        ("खुशी", ["खुश"]), ("उदास", []), ("पार्टी", []),
        ("वर्कआउट", []), ("सुबह", []), ("शाम", []),
        ("डिनर", []), ("नींद", ["स्लीप"]),
    ],
    "ar-SA": [
        ("استرخاء", []), ("هادئ", []), ("مبهج", []),
        ("حيوي", []), ("تركيز", []), ("رومانسي", []),
        ("سعيد", []), ("حزين", []), ("حفلة", []),
        ("تمرين", []), ("صباح", []), ("مساء", []),
        ("عشاء", []), ("نوم", []),
    ],
}


def has_template(locale):
    return os.path.exists(os.path.join(TEMPLATES_DIR, f"{locale}.yaml"))


def resolve_moods(locale):
    spec = LOCALE_MOODS.get(locale)
    if isinstance(spec, str):  # alias to another locale
        return LOCALE_MOODS[spec]
    return spec


def transform(locale):
    if has_template(locale):
        # Template-owned since JF-316: the template is the one writer of
        # this model. Keep this guard so a table entry that outlives its
        # locale's templating milestone cannot resurrect a second writer.
        print(f"  [{locale}] SKIP: templates/{locale}.yaml owns the Mood "
              "block; edit the template + run generate_interaction_model.py")
        return False
    moods = resolve_moods(locale)
    if not moods:
        print(f"  [{locale}] no mood table, SKIP")
        return False
    path = os.path.join(MODELS_DIR, f"model_{locale}.json")
    with open(path) as f:
        doc = json.load(f)
    lm = doc["languageModel"]
    types = lm.setdefault("types", [])

    # 1. Replace or insert the Mood slot type.
    mood_type = {"name": "Mood", "values": []}
    for value, syns in moods:
        name = {"value": value}
        if syns:
            name["synonyms"] = list(syns)
        mood_type["values"].append({"name": name})
    for i, t in enumerate(types):
        if t.get("name") == "Mood":
            types[i] = mood_type
            break
    else:
        types.insert(0, mood_type)

    changed = False
    # 2. Change PlayMoodMusic mood slot type -> Mood.
    for it in lm["intents"]:
        if it["name"] != "PlayMoodMusicIntent":
            continue
        for sl in it.get("slots", []):
            if sl["name"] == "mood" and sl["type"] != "Mood":
                sl["type"] = "Mood"
                changed = True
        # 3. Keep only slotted samples (drop concrete sample-less utterances).
        before = len(it.get("samples", []))
        it["samples"] = [s for s in it.get("samples", []) if "{" in s]
        if before != len(it["samples"]):
            changed = True

    if changed or True:  # always rewrite to apply the type replacement
        with open(path, "w") as f:
            json.dump(doc, f, ensure_ascii=False, indent=2)
            f.write("\n")
    return changed


def main():
    for locale in sorted(LOCALE_MOODS.keys()):
        ch = transform(locale)
        if not has_template(locale):
            print(f"  [{locale}] {'transformed' if ch else 'no change'}")


if __name__ == "__main__":
    main()

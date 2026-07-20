#!/usr/bin/env python3
"""JF-356: roll the custom Mood slot type to the 16 non-it-IT locales.

For each locale:
  1. Add (or replace) a `Mood` custom slot type with locale-specific values.
  2. Change PlayMoodMusicIntent's `mood` slot from AMAZON.SearchQuery -> Mood.
  3. Keep only slotted mood samples (drop concrete sample-less utterances like
     "play morning music" that leave the slot empty -> anti-pattern #1).

Per-locale mood values reuse the words already in LocalizedMoodMap (de/es/fr/pt/it)
where available, plus translations for the locales the map doesn't cover
(nl-NL, ja-JP, hi-IN, ar-SA, es-MX, es-US, fr-CA). English locales use the
MoodGenreMap keys directly.

Each entry is a list of (value, [synonyms]) tuples. The handler reads the raw
spoken text (moodSlot.Value), so every value AND every synonym must independently
resolve via MoodGenreMap (English key) or LocalizedMoodMap — that mapping lives
in PlayMoodMusicIntentHandler.cs and is locale-agnostic at lookup time, so any
translated word resolves through tier-2 (localized->English) ONLY if present in
LocalizedMoodMap. Translated words NOT in LocalizedMoodMap fall to tier-5
(raw-mood-as-genre) — which works only if the word coincides with a Jellyfin
genre name. Therefore, for locales whose words aren't in LocalizedMoodMap,
we ALSO add the words to LocalizedMoodMap in the handler (separate edit).
"""
import json
import os

import os
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODELS_DIR = os.path.join(REPO, "Jellyfin.Plugin.AlexaSkill", "Alexa", "InteractionModel")

# Per-locale mood values: list of (canonical_value, [synonyms]).
# Built from LocalizedMoodMap (de/it/es/fr/pt) + curated translations for the rest.
# English keys (relaxing, chill, upbeat, energetic, focus, romantic, happy, sad,
# party, workout, morning, evening, dinner, sleep) are the resolve targets.
LOCALE_MOODS = {
    "en-US": [
        ("relaxing", []), ("chill", []), ("upbeat", []), ("energetic", []),
        ("focus", []), ("romantic", []), ("happy", []), ("sad", []),
        ("party", []), ("workout", []), ("morning", []), ("evening", []),
        ("dinner", []), ("sleep", []),
    ],
    # en-GB/AU/CA/IN share the English set.
    "en-GB": None, "en-AU": None, "en-CA": None, "en-IN": None,

    "de-DE": [
        ("entspannend", ["entspannt"]), ("beruhigend", []), ("beschwingt", []),
        ("energisch", []), ("fokus", []), ("romantisch", []),
        ("fröhlich", ["glücklich"]), ("traurig", []), ("feier", ["party"]),
        ("training", []), ("morgens", []), ("abend", ["abends"]),
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

    "it-IT": None,  # already done in JF-354 (skip)

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


def resolve_moods(locale):
    spec = LOCALE_MOODS.get(locale)
    if locale in ("en-GB", "en-AU", "en-CA", "en-IN"):
        return LOCALE_MOODS["en-US"]
    if isinstance(spec, str):  # alias to another locale
        return LOCALE_MOODS[spec]
    return spec


def transform(locale):
    if locale == "it-IT":
        return False  # already done
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
        if locale == "it-IT":
            continue
        ch = transform(locale)
        print(f"  [{locale}] {'transformed' if ch else 'no change'}")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Generate docs/VOICE_COMMANDS_BY_LOCALE.md from the 17 interaction models.

The complete per-locale invocation reference (JF-512). Unlike VOICE_COMMANDS.md
(the hand-maintained mirror that went stale twice, JF-459/JF-494), this doc is
EMITTED from model_<locale>.json so it cannot drift: every sample of every
intent of every locale, grouped by what the user wants to do, with slot
placeholders rendered as readable localized hints.

Body is user-facing: no handler/class/intent names outside the appendix.
The appendix maps every group heading and command label back to the intent
name in the model for maintainers.

Usage:
  python3 scripts/generate_voice_reference.py           # (re)write the doc
  python3 scripts/generate_voice_reference.py --check   # exit 1 if stale

Determinism: output depends only on the model JSONs (no timestamps), so
running twice produces no diff. CI runs --check in the validate-models job
and in release-build (wired 2026-09-12, JF-513.1).
"""

import argparse
import json
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
MODELS_DIR = REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel"
OUT_PATH = REPO / "docs" / "VOICE_COMMANDS_BY_LOCALE.md"

# Locale display order: by language name, mirroring VOICE_COMMANDS.md.
LOCALE_ORDER = [
    "ar-SA", "nl-NL", "en-AU", "en-CA", "en-GB", "en-IN", "en-US",
    "fr-CA", "fr-FR", "de-DE", "hi-IN", "it-IT", "ja-JP", "pt-BR",
    "es-ES", "es-MX", "es-US",
]

LANGUAGE_NAMES = {
    "ar": "Arabic", "nl": "Dutch", "en": "English", "fr": "French",
    "de": "German", "hi": "Hindi", "it": "Italian", "ja": "Japanese",
    "pt": "Portuguese", "es": "Spanish",
}

# Default invocation names. Source of truth: Jellyfin.Plugin.AlexaSkill/Config.cs
#   Config.LocaleInvocationNames["it-IT"] = "mia collezione"
#   Config.InvocationName = "jellyfin player"  (every other locale)
# A per-user custom invocation name set in the plugin replaces the default in
# ALL locales (Config.EffectiveInvocationName).
LOCALE_INVOCATION_NAMES = {"it-IT": "mia collezione"}
DEFAULT_INVOCATION_NAME = "jellyfin player"
CONFIG_CITATION = (
    "Defaults come from `Config.LocaleInvocationNames` (it-IT) and "
    "`Config.InvocationName` (all other locales) in "
    "`Jellyfin.Plugin.AlexaSkill/Config.cs`"
)

# Locales whose model carries an infinitive one-shot sample layer (JF-493),
# with the marker that opens those samples in that language.
INFINITIVE_MARKERS = {
    "it": "Di",
    "en": "to",
    "de": "Zu",
    "fr": "De",
}

# Documented one-shot carriers (CLAUDE.md "invocation name" section): only
# it-IT and the English locales have a verified phrasing; the other infinitive
# locales get the generic sentence with their marker named.
ONESHOT_EXAMPLES = {
    "it": 'Alexa, chiedi a mia collezione di suonare il brano <titolo del brano>',
    "en": "Alexa, ask jellyfin player to play the song <song title>",
}

# Stable grouping map: user-facing group heading -> ordered (intent, label) pairs.
# Built-ins are flagged True so the renderer can group the sample-less ones
# under a single note (Alexa understands them natively; the model adds no
# custom phrases, except it-IT StopIntent which carries 6 samples).
GROUPS = [
    ("Play music", [
        ("PlaySongIntent", "Play a song", False),
        ("PlayArtistSongsIntent", "Play an artist", False),
        ("PlayByGenreIntent", "Play by genre", False),
        ("PlayByDecadeIntent", "Play by decade", False),
        ("PlayMoodMusicIntent", "Play music for a mood", False),
        ("PlayPlaylistIntent", "Play a playlist", False),
        ("ShufflePlayIntent", "Shuffle a playlist", False),
        ("PlayRandomIntent", "Play something random", False),
        ("PlayLastAddedIntent", "Play recently added media", False),
    ]),
    ("Play albums", [
        ("PlayAlbumIntent", "Play an album", False),
    ]),
    ("Play videos", [
        ("PlayVideoIntent", "Play a video or movie", False),
    ]),
    ("Episodes and series", [
        ("PlayEpisodeIntent", "Play an episode", False),
        ("PlayNextEpisodeIntent", "Play the next episode", False),
    ]),
    ("Podcasts", [
        ("PlayPodcastIntent", "Play a podcast", False),
    ]),
    ("Audiobooks", [
        ("PlayBookIntent", "Play an audiobook", False),
    ]),
    ("Radio and live TV", [
        ("PlayRadioIntent", "Start radio mode", False),
        ("TurnRadioOnIntent", "Turn radio mode on", False),
        ("TurnRadioOffIntent", "Turn radio mode off", False),
        ("PlayChannelIntent", "Play a live TV channel", False),
    ]),
    ("Search", [
        ("SearchMediaIntent", "Search the library", False),
        ("FindSongIntent", "Find a song (multi-turn conversation)", False),
        ("FindSongByArtistIntent", "Find a song by an artist", False),
    ]),
    ("Queue and repeat", [
        ("AddToQueueIntent", "Add to the queue", False),
        ("PlayNextIntent", "Play next", False),
        ("ClearQueueIntent", "Clear the queue", False),
        ("ListQueueIntent", "List the queue", False),
        ("LoopSongOnIntent", "Loop the current song", False),
        ("RepeatSingleOnIntent", "Repeat the current track", False),
        ("LoopAllOnIntent", "Repeat all", False),
        ("LoopAllOffIntent", "Turn repeat off", False),
    ]),
    ("Favorites", [
        ("PlayFavoritesIntent", "Play favorites", False),
        ("MarkFavoriteIntent", "Mark as favorite", False),
        ("UnmarkFavoriteIntent", "Remove from favorites", False),
    ]),
    ("Info and queries", [
        ("MediaInfoIntent", "Ask about what is playing", False),
        ("QueryArtistLibraryIntent", "Ask what the library has by an artist", False),
    ]),
    ("Browse and discover", [
        ("BrowseLibraryIntent", "Browse the library", False),
        ("RecommendIntent", "Get a recommendation", False),
        ("QueryRecentlyAddedIntent", "Ask what is new", False),
        ("ShowMoreIntent", "Show more", False),
    ]),
    ("Resume and continue", [
        ("ContinueWatchingIntent", "Continue watching or listening", False),
        ("InProgressMediaListIntent", "List what is in progress", False),
    ]),
    ("Playback control", [
        ("GoToChapterIntent", "Go to a chapter", False),
        ("SkipForwardBackIntent", "Skip forward or back", False),
        ("JumpToPositionIntent", "Jump to a position", False),
        ("FollowMeIntent", "Follow me (move playback here)", False),
        ("AMAZON.PauseIntent", "Pause", True),
        ("AMAZON.ResumeIntent", "Resume", True),
        ("AMAZON.StopIntent", "Stop", True),
        ("AMAZON.NextIntent", "Next track", True),
        ("AMAZON.PreviousIntent", "Previous track", True),
        ("AMAZON.ShuffleOnIntent", "Shuffle on", True),
        ("AMAZON.ShuffleOffIntent", "Shuffle off", True),
        ("AMAZON.StartOverIntent", "Start over", True),
        ("AMAZON.LoopOnIntent", "Repeat all on", True),
        ("AMAZON.LoopOffIntent", "Repeat all off", True),
    ]),
    ("Timers and reminders", [
        ("SleepTimerIntent", "Set a sleep timer", False),
        ("SetReminderIntent", "Set a reminder", False),
    ]),
    ("Account and voice", [
        ("LearnMyVoiceIntent", "Learn my voice", False),
        ("WhoAmIIntent", "Who am I", False),
    ]),
    ("Session and conversation", [
        ("AMAZON.HelpIntent", "Help", True),
        ("AMAZON.CancelIntent", "Cancel", True),
        ("AMAZON.FallbackIntent", "Fallback", True),
        ("AMAZON.YesIntent", "Yes", True),
        ("AMAZON.NoIntent", "No", True),
    ]),
]

# Readable localized placeholder for each slot name, by language prefix.
# The legend and the phrase bodies both use these; a slot missing here fails
# generation (new slots must get a hint, the same way new intents must get a
# group) so the doc can never degrade to raw identifiers.
SLOT_HINTS = {
    "en": {
        "song": "song title", "musician": "artist name", "album": "album title",
        "book": "audiobook title", "genre": "music genre",
        "decade": "decade (the 80s)", "mood": "mood (relaxed, energetic)",
        "playlist": "playlist name", "podcast_name": "podcast name",
        "title": "video or movie title", "channel": "channel name",
        "station": "radio station name", "series_name": "series name",
        "season_number": "season number", "episode_number": "episode number",
        "chapter_number": "chapter number",
        "direction": "ordinal (first, second)",
        "duration_minutes": "number of minutes", "reminder_time": "time (7 pm)",
        "media_type": "media type (music, video)",
        "media_info_type": "what to ask (title, artist, album)",
        "browse_category": "category (artists, albums, movies)",
        "query_type": "tracks or albums", "query": "what to search for",
        "titleKeywords": "words from the song title", "username": "user name",
        "time_period": "time period (today, this week)",
        "seek_amount": "number", "seek_direction": "forward or back",
        "seek_unit": "seconds or minutes", "position_hours": "hours",
        "position_minutes": "minutes", "position_seconds": "seconds",
    },
    "it": {
        "song": "titolo del brano", "musician": "nome dell'artista",
        "album": "titolo dell'album", "book": "titolo dell'audiolibro",
        "genre": "genere musicale", "decade": "decennio (gli anni ottanta)",
        "mood": "umore (rilassato, energico)",
        "playlist": "nome della playlist", "podcast_name": "nome del podcast",
        "title": "titolo del video o del film", "channel": "nome del canale",
        "station": "nome della stazione radio", "series_name": "nome della serie",
        "season_number": "numero della stagione",
        "episode_number": "numero dell'episodio",
        "chapter_number": "numero del capitolo",
        "direction": "ordinale (primo, secondo)",
        "duration_minutes": "numero di minuti", "reminder_time": "ora (alle sette)",
        "media_type": "tipo di contenuto (musica, video)",
        "media_info_type": "informazione (titolo, artista, album)",
        "browse_category": "categoria (artisti, album, film)",
        "query_type": "brani o album", "query": "testo da cercare",
        "titleKeywords": "parole del titolo del brano",
        "username": "nome dell'utente",
        "time_period": "periodo (oggi, questa settimana)",
        "seek_amount": "quantità", "seek_direction": "avanti o indietro",
        "seek_unit": "secondi o minuti", "position_hours": "ore",
        "position_minutes": "minuti", "position_seconds": "secondi",
    },
    "de": {
        "song": "Titel des Liedes", "musician": "Name des Künstlers",
        "album": "Titel des Albums", "book": "Titel des Hörbuchs",
        "genre": "Musikgenre", "decade": "Jahrzehnt (die 80er)",
        "mood": "Stimmung (entspannt, energiegeladen)",
        "playlist": "Name der Playlist", "podcast_name": "Name des Podcasts",
        "title": "Titel des Videos oder Films",
        "channel": "Name des Senders", "station": "Name des Radiosenders",
        "series_name": "Name der Serie", "season_number": "Staffelnummer",
        "episode_number": "Episodennummer", "chapter_number": "Kapitelnummer",
        "direction": "Ordinalzahl (erstes, zweites)",
        "duration_minutes": "Anzahl Minuten",
        "reminder_time": "Uhrzeit (um 19 Uhr)",
        "media_type": "Medientyp (Musik, Video)",
        "media_info_type": "gewünschte Info (Titel, Künstler, Album)",
        "browse_category": "Kategorie (Künstler, Alben, Filme)",
        "query_type": "Titel oder Alben", "query": "Suchbegriff",
        "titleKeywords": "Wörter aus dem Songtitel",
        "username": "Benutzername",
        "time_period": "Zeitraum (heute, diese Woche)",
        "seek_amount": "Anzahl", "seek_direction": "vor oder zurück",
        "seek_unit": "Sekunden oder Minuten", "position_hours": "Stunden",
        "position_minutes": "Minuten", "position_seconds": "Sekunden",
    },
    "es": {
        "song": "título de la canción", "musician": "nombre del artista",
        "album": "título del álbum", "book": "título del audiolibro",
        "genre": "género musical", "decade": "década (los ochenta)",
        "mood": "ánimo (relajado, energético)",
        "playlist": "nombre de la lista",
        "podcast_name": "nombre del pódcast",
        "title": "título del vídeo o de la película",
        "channel": "nombre del canal", "station": "nombre de la emisora",
        "series_name": "nombre de la serie", "season_number": "número de temporada",
        "episode_number": "número de episodio",
        "chapter_number": "número de capítulo",
        "direction": "ordinal (primero, segundo)",
        "duration_minutes": "número de minutos",
        "reminder_time": "hora (a las siete)",
        "media_type": "tipo de contenido (música, vídeo)",
        "media_info_type": "dato (título, artista, álbum)",
        "browse_category": "categoría (artistas, álbumes, películas)",
        "query_type": "canciones o álbumes", "query": "texto a buscar",
        "titleKeywords": "palabras del título de la canción",
        "username": "nombre de usuario",
        "time_period": "periodo (hoy, esta semana)",
        "seek_amount": "cantidad", "seek_direction": "adelante o atrás",
        "seek_unit": "segundos o minutos", "position_hours": "horas",
        "position_minutes": "minutos", "position_seconds": "segundos",
    },
    "fr": {
        "song": "titre de la chanson", "musician": "nom de l'artiste",
        "album": "titre de l'album", "book": "titre du livre audio",
        "genre": "genre musical", "decade": "décennie (les années 80)",
        "mood": "ambiance (détendu, énergique)",
        "playlist": "nom de la playlist", "podcast_name": "nom du podcast",
        "title": "titre de la vidéo ou du film",
        "channel": "nom de la chaîne",
        "station": "nom de la station de radio",
        "series_name": "nom de la série", "season_number": "numéro de saison",
        "episode_number": "numéro d'épisode",
        "chapter_number": "numéro de chapitre",
        "direction": "ordinal (premier, deuxième)",
        "duration_minutes": "nombre de minutes",
        "reminder_time": "heure (à 19 heures)",
        "media_type": "type de média (musique, vidéo)",
        "media_info_type": "information (titre, artiste, album)",
        "browse_category": "catégorie (artistes, albums, films)",
        "query_type": "chansons ou albums", "query": "texte à rechercher",
        "titleKeywords": "mots du titre de la chanson",
        "username": "nom d'utilisateur",
        "time_period": "période (aujourd'hui, cette semaine)",
        "seek_amount": "quantité", "seek_direction": "en avant ou en arrière",
        "seek_unit": "secondes ou minutes", "position_hours": "heures",
        "position_minutes": "minutes", "position_seconds": "secondes",
    },
    "pt": {
        "song": "título da música", "musician": "nome do artista",
        "album": "título do álbum", "book": "título do audiolivro",
        "genre": "gênero musical", "decade": "década (anos 80)",
        "mood": "humor (relaxado, energético)",
        "playlist": "nome da playlist", "podcast_name": "nome do podcast",
        "title": "título do vídeo ou do filme", "channel": "nome do canal",
        "station": "nome da estação de rádio",
        "series_name": "nome da série", "season_number": "número da temporada",
        "episode_number": "número do episódio",
        "chapter_number": "número do capítulo",
        "direction": "ordinal (primeiro, segundo)",
        "duration_minutes": "número de minutos", "reminder_time": "hora (às sete)",
        "media_type": "tipo de mídia (música, vídeo)",
        "media_info_type": "informação (título, artista, álbum)",
        "browse_category": "categoria (artistas, álbuns, filmes)",
        "query_type": "faixas ou álbuns", "query": "texto a procurar",
        "titleKeywords": "palavras do título da música",
        "username": "nome do usuário",
        "time_period": "período (hoje, esta semana)",
        "seek_amount": "quantidade", "seek_direction": "avançar ou voltar",
        "seek_unit": "segundos ou minutos", "position_hours": "horas",
        "position_minutes": "minutos", "position_seconds": "segundos",
    },
    "nl": {
        "song": "titel van het nummer", "musician": "naam van de artiest",
        "album": "titel van het album", "book": "titel van het luisterboek",
        "genre": "muziekgenre", "decade": "decennium (jaren 80)",
        "mood": "stemming (relaxt, energiek)",
        "playlist": "naam van de playlist", "podcast_name": "naam van de podcast",
        "title": "titel van de video of film",
        "channel": "naam van het kanaal",
        "station": "naam van het radiostation",
        "series_name": "naam van de serie", "season_number": "seizoensnummer",
        "episode_number": "afleveringsnummer",
        "chapter_number": "hoofdstuknummer",
        "direction": "rangtelwoord (eerste, tweede)",
        "duration_minutes": "aantal minuten",
        "reminder_time": "tijdstip (om 7 uur)",
        "media_type": "mediatype (muziek, video)",
        "media_info_type": "info (titel, artiest, album)",
        "browse_category": "categorie (artiesten, albums, films)",
        "query_type": "nummers of albums", "query": "zoektekst",
        "titleKeywords": "woorden uit de songtitel",
        "username": "gebruikersnaam",
        "time_period": "periode (vandaag, deze week)",
        "seek_amount": "aantal", "seek_direction": "vooruit of terug",
        "seek_unit": "seconden of minuten", "position_hours": "uren",
        "position_minutes": "minuten", "position_seconds": "seconden",
    },
    "ja": {
        "song": "曲名", "musician": "アーティスト名", "album": "アルバム名",
        "book": "オーディオブック名", "genre": "ジャンル",
        "decade": "年代 (80年代)", "mood": "ムード (リラックス、エネルギッシュ)",
        "playlist": "プレイリスト名", "podcast_name": "ポッドキャスト名",
        "title": "動画または映画のタイトル", "channel": "チャンネル名",
        "station": "ラジオ局名", "series_name": "シリーズ名",
        "season_number": "シーズン番号", "episode_number": "エピソード番号",
        "chapter_number": "チャプター番号", "direction": "順序 (最初、次)",
        "duration_minutes": "分数", "reminder_time": "時刻 (7時)",
        "media_type": "メディアの種類 (音楽、ビデオ)",
        "media_info_type": "情報の種類 (タイトル、アーティスト、アルバム)",
        "browse_category": "カテゴリ (アーティスト、アルバム、映画)",
        "query_type": "トラックまたはアルバム", "query": "検索したい言葉",
        "titleKeywords": "曲名のキーワード", "username": "ユーザー名",
        "time_period": "期間 (今日、今週)", "seek_amount": "数",
        "seek_direction": "前または後ろ", "seek_unit": "秒または分",
        "position_hours": "時間", "position_minutes": "分",
        "position_seconds": "秒",
    },
    "hi": {
        "song": "गाने का नाम", "musician": "कलाकार का नाम",
        "album": "एल्बम का नाम", "book": "ऑडियोबुक का नाम",
        "genre": "संगीत शैली", "decade": "दशक (80 का दशक)",
        "mood": "मूड (शांत, ऊर्जावान)", "playlist": "प्लेलिस्ट का नाम",
        "podcast_name": "पॉडकास्ट का नाम",
        "title": "वीडियो या फिल्म का नाम", "channel": "चैनल का नाम",
        "station": "रेडियो स्टेशन का नाम", "series_name": "सीरीज़ का नाम",
        "season_number": "सीज़न नंबर", "episode_number": "एपिसोड नंबर",
        "chapter_number": "अध्याय नंबर",
        "direction": "क्रम (पहला, दूसरा)",
        "duration_minutes": "मिनट की संख्या",
        "reminder_time": "समय (शाम 7 बजे)",
        "media_type": "मीडिया प्रकार (म्यूज़िक, वीडियो)",
        "media_info_type": "जानकारी (शीर्षक, कलाकार, एल्बम)",
        "browse_category": "श्रेणी (कलाकार, एल्बम, फिल्में)",
        "query_type": "गाने या एल्बम", "query": "खोजने के लिए शब्द",
        "titleKeywords": "गाने के नाम के शब्द",
        "username": "उपयोगकर्ता का नाम",
        "time_period": "समयावधि (आज, इस सप्ताह)",
        "seek_amount": "संख्या", "seek_direction": "आगे या पीछे",
        "seek_unit": "सेकंड या मिनट", "position_hours": "घंटे",
        "position_minutes": "मिनट", "position_seconds": "सेकंड",
    },
    "ar": {
        "song": "اسم الأغنية", "musician": "اسم الفنان",
        "album": "عنوان الألبوم", "book": "عنوان الكتاب الصوتي",
        "genre": "النوع الموسيقي", "decade": "العقد (الثمانينيات)",
        "mood": "المزاج (هادئ، نشيط)",
        "playlist": "اسم قائمة التشغيل", "podcast_name": "اسم البودكاست",
        "title": "عنوان الفيديو أو الفيلم", "channel": "اسم القناة",
        "station": "اسم محطة الراديو", "series_name": "اسم المسلسل",
        "season_number": "رقم الموسم", "episode_number": "رقم الحلقة",
        "chapter_number": "رقم الفصل",
        "direction": "ترتيبي (الأول، الثاني)",
        "duration_minutes": "عدد الدقائق",
        "reminder_time": "الوقت (الساعة السابعة)",
        "media_type": "نوع الوسائط (موسيقى، فيديو)",
        "media_info_type": "المعلومة (العنوان، الفنان، الألبوم)",
        "browse_category": "الفئة (فنانون، ألبومات، أفلام)",
        "query_type": "أغانٍ أو ألبومات", "query": "نص البحث",
        "titleKeywords": "كلمات من عنوان الأغنية",
        "username": "اسم المستخدم",
        "time_period": "الفترة (اليوم، هذا الأسبوع)",
        "seek_amount": "عدد", "seek_direction": "للأمام أو للخلف",
        "seek_unit": "ثوانٍ أو دقائق", "position_hours": "ساعات",
        "position_minutes": "دقائق", "position_seconds": "ثوانٍ",
    },
}

SLOT_IN_SAMPLE_RE = re.compile(r"\{([A-Za-z_][A-Za-z0-9_]*)\}")


def load_models():
    """Return {locale: languageModel dict} for the 17 locales, sorted per LOCALE_ORDER."""
    models = {}
    for path in sorted(MODELS_DIR.glob("model_*.json")):
        locale = path.stem[len("model_"):]
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        models[locale] = data.get("interactionModel", data)["languageModel"]
    missing = [loc for loc in LOCALE_ORDER if loc not in models]
    if missing:
        sys.exit(f"ERROR: models missing for locales: {missing}")
    # Review fix (JF-512, confidence 85): the check was one-directional. A NEW locale
    # model file (pt-PT, es-AR, ...) otherwise slips every guard - both validators
    # pass on existing hints, the emitter walks LOCALE_ORDER, --check stays green -
    # and the doc silently omits a shipped locale while claiming completeness.
    unknown = sorted(loc for loc in models if loc not in LOCALE_ORDER)
    if unknown:
        sys.exit(
            f"ERROR: model files exist for locales not in LOCALE_ORDER: {unknown}. "
            "Add them to LOCALE_ORDER (and their slot hints) so the doc emits them."
        )
    return models


def validate_grouping(models):
    """Every intent of every model must map to exactly one group entry."""
    mapped = {}
    for heading, entries in GROUPS:
        for intent, _label, _builtin in entries:
            if intent in mapped:
                sys.exit(f"ERROR: intent {intent} listed in two groups")
            mapped[intent] = heading
    unmapped = set()
    present = set()
    for _locale, lm in models.items():
        for intent in lm["intents"]:
            present.add(intent["name"])
            if intent["name"] not in mapped:
                unmapped.add(intent["name"])
    if unmapped:
        sys.exit(
            "ERROR: intents present in models but missing from GROUPS "
            f"(add each to a group in scripts/generate_voice_reference.py): {sorted(unmapped)}"
        )
    # Review fix (JF-512, minor): the reverse direction was unenforced; an intent
    # removed from every model left a stale GROUPS entry rendering a phantom appendix
    # row. A GROUPS entry may only reference intents that exist somewhere.
    stale = sorted(name for name in mapped if name not in present)
    if stale:
        sys.exit(
            "ERROR: GROUPS lists intents no model carries anymore "
            f"(remove their entries): {stale}"
        )


def slots_in_samples(lm):
    """Set of slot names referenced by any sample of any intent in the model."""
    slots = set()
    for intent in lm["intents"]:
        for sample in intent.get("samples", []) or []:
            slots.update(SLOT_IN_SAMPLE_RE.findall(sample))
    return slots


def validate_slot_hints(models):
    """Every slot used in a locale must have a hint in that locale's language."""
    problems = []
    for locale, lm in models.items():
        lang = locale[:2]
        hints = SLOT_HINTS.get(lang)
        if hints is None:
            problems.append(f"{locale}: no SLOT_HINTS table for language '{lang}'")
            continue
        for slot in sorted(slots_in_samples(lm)):
            if slot not in hints:
                problems.append(f"{locale}: slot '{slot}' has no hint in SLOT_HINTS['{lang}']")
    if problems:
        sys.exit("ERROR: unmapped slots (add hints to SLOT_HINTS):\n  " + "\n  ".join(problems))


def render_sample(sample, hints):
    def repl(match):
        return f"<{hints[match.group(1)]}>"

    return SLOT_IN_SAMPLE_RE.sub(repl, sample)


def invocation_note(locale):
    name = LOCALE_INVOCATION_NAMES.get(locale, DEFAULT_INVOCATION_NAME)
    return (
        f'Default invocation name: **"{name}"**. A custom invocation name set in the '
        "plugin settings replaces this default in every locale."
    )


def oneshot_note(locale):
    lang = locale[:2]
    name = LOCALE_INVOCATION_NAMES.get(locale, DEFAULT_INVOCATION_NAME)
    example = ONESHOT_EXAMPLES.get(lang)
    if example:
        return (
            f"Any phrase can be used one-shot by prefixing the invocation name, "
            f"for example `{example}`. Samples that start with "
            f"`{INFINITIVE_MARKERS[lang]} ` are infinitive forms meant exactly "
            "for this one-shot use."
        )
    if lang in INFINITIVE_MARKERS:
        return (
            f"Any phrase can be used one-shot by addressing `{name}` directly. "
            f"Samples that start with `{INFINITIVE_MARKERS[lang]} ` are infinitive "
            "forms meant exactly for this one-shot use."
        )
    return (
        f"Any phrase can be used one-shot by saying Alexa, then the invocation "
        f"name `{name}`, then the phrase."
    )


def locale_section(locale, lm):
    lang = locale[:2]
    hints = SLOT_HINTS[lang]
    lines = []
    lines.append(f'### <a id="{locale.lower()}"></a>{LANGUAGE_NAMES[lang]} ({locale})')
    lines.append("")
    lines.append(invocation_note(locale))
    lines.append("")
    lines.append(oneshot_note(locale))
    lines.append("")

    # Slot legend: only the slots this locale's phrases actually use.
    used_slots = sorted(slots_in_samples(lm))
    lines.append("Placeholder legend:")
    lines.append("")
    lines.append("| Placeholder in the phrases | Model slot |")
    lines.append("| --- | --- |")
    for slot in used_slots:
        lines.append(f"| `<{hints[slot]}>` | `{{{slot}}}` |")
    lines.append("")

    intents_by_name = {i["name"]: i for i in lm["intents"]}
    total_samples = sum(len(i.get("samples", []) or []) for i in lm["intents"])
    lines.append(
        f"Complete phrase list ({total_samples} phrases across "
        f"{len(lm['intents'])} commands):"
    )
    lines.append("")

    for heading, entries in GROUPS:
        present = [(n, lbl, b) for (n, lbl, b) in entries if n in intents_by_name]
        if not present:
            continue
        lines.append(f"#### {heading}")
        lines.append("")
        builtin_without_samples = []
        for intent_name, label, is_builtin in present:
            samples = intents_by_name[intent_name].get("samples", []) or []
            if is_builtin and not samples:
                builtin_without_samples.append(label)
                continue
            lines.append(f"**{label}**")
            lines.append("")
            for sample in samples:
                lines.append(f"- `{render_sample(sample, hints)}`")
            lines.append("")
        if builtin_without_samples:
            lines.append(
                "**Built-in commands, no custom phrases in this language** "
                f"({', '.join(builtin_without_samples)}): Alexa understands these "
                "natively; say them the usual way."
            )
            lines.append("")
    return "\n".join(lines)


def language_index():
    lines = ["### Language index", ""]
    by_language = {}
    for locale in LOCALE_ORDER:
        by_language.setdefault(LANGUAGE_NAMES[locale[:2]], []).append(locale)
    for language in sorted(by_language):
        links = ", ".join(
            f"[{loc}](#{loc.lower()})" for loc in by_language[language]
        )
        lines.append(f"- **{language}**: {links}")
    lines.append("")
    return "\n".join(lines)


def appendix():
    lines = [
        "## Appendix: labels and intent names (for maintainers)",
        "",
        "The body above is user-facing and never names intents or handlers. "
        "This table maps every group heading and command label back to the "
        "intent name in the interaction models.",
        "",
        "| Group | Command label | Intent name |",
        "| --- | --- | --- |",
    ]
    for heading, entries in GROUPS:
        for intent_name, label, _builtin in entries:
            lines.append(f"| {heading} | {label} | `{intent_name}` |")
    lines.append("")
    lines.append(
        "Invocation-name defaults live in `Jellyfin.Plugin.AlexaSkill/Config.cs` "
        "(`Config.LocaleInvocationNames` for it-IT, `Config.InvocationName` "
        "otherwise); a per-user custom name overrides them everywhere via "
        "`Config.EffectiveInvocationName`."
    )
    lines.append("")
    return "\n".join(lines)


def generate(models):
    parts = []
    parts.append("<!-- Generated by scripts/generate_voice_reference.py. DO NOT EDIT BY HAND. -->")
    parts.append("# Voice Commands by Locale")
    parts.append("")
    parts.append(
        "Every voice phrase the skill understands, in all 17 supported locales, "
        "generated directly from the interaction models so it can never go stale "
        "(the hand-maintained [VOICE_COMMANDS.md](../VOICE_COMMANDS.md) went "
        "stale twice). Regenerate after any model change:"
    )
    parts.append("")
    parts.append("```bash")
    parts.append("python3 scripts/generate_voice_reference.py            # rewrite this doc")
    parts.append("python3 scripts/generate_voice_reference.py --check   # exit 1 if stale")
    parts.append("```")
    parts.append("")
    parts.append(
        "Source of truth: `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/"
        "model_<locale>.json`. Phrases are listed exactly as the models carry "
        "them; slot placeholders are rendered as readable hints explained in "
        "each locale's legend."
    )
    parts.append("")
    parts.append(
        "Invocation names: the skill answers to **\"mia collezione\"** in Italian "
        f"and **\"{DEFAULT_INVOCATION_NAME}\"** everywhere else. {CONFIG_CITATION}; "
        "a custom name set in the plugin settings replaces the default in all "
        "locales."
    )
    parts.append("")
    parts.append(language_index())
    for locale in LOCALE_ORDER:
        parts.append(locale_section(locale, models[locale]))
    parts.append(appendix())
    return "\n".join(parts).rstrip("\n") + "\n"


def main():
    parser = argparse.ArgumentParser(description=(__doc__ or "Generate the voice command reference.").splitlines()[0])
    parser.add_argument(
        "--check",
        action="store_true",
        help="do not write; exit 1 if docs/VOICE_COMMANDS_BY_LOCALE.md differs from the models",
    )
    args = parser.parse_args()

    models = load_models()
    validate_grouping(models)
    validate_slot_hints(models)
    content = generate(models)

    if args.check:
        if not OUT_PATH.exists():
            print(f"STALE: {OUT_PATH} does not exist; run the generator")
            return 1
        current = OUT_PATH.read_text(encoding="utf-8")
        if current != content:
            print(
                f"STALE: {OUT_PATH} differs from what the current models generate; "
                "run scripts/generate_voice_reference.py and commit the result"
            )
            return 1
        print(f"OK: {OUT_PATH} matches the current models")
        return 0

    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUT_PATH.write_text(content, encoding="utf-8")
    total = sum(
        len(i.get("samples", []) or []) for lm in models.values() for i in lm["intents"]
    )
    print(f"Wrote {OUT_PATH} ({len(content.splitlines())} lines, {total} phrases)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

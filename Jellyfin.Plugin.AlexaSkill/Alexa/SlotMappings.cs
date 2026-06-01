using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Maps Alexa slot canonical values (all locales) to Jellyfin domain types.
/// Keys are lowercase — callers normalize via <c>.ToLowerInvariant()</c>.
/// </summary>
internal static class SlotMappings
{
    private static readonly BaseItemKind[] VideoKinds = [BaseItemKind.Movie, BaseItemKind.Episode];
    private static readonly BaseItemKind[] AudioKinds = [BaseItemKind.Audio];

    public static FrozenDictionary<string, BaseItemKind?> BrowseCategoryToItemKind { get; } =
        new Dictionary<string, BaseItemKind?>
        {
            { "artists", BaseItemKind.MusicArtist },
            { "artisti", BaseItemKind.MusicArtist },
            { "cantanti", BaseItemKind.MusicArtist },
            { "gruppi", BaseItemKind.MusicArtist },
            { "artistas", BaseItemKind.MusicArtist },
            { "artistes", BaseItemKind.MusicArtist },
            { "künstler", BaseItemKind.MusicArtist },
            { "artiesten", BaseItemKind.MusicArtist },
            { "فنانون", BaseItemKind.MusicArtist },
            { "कलाकार", BaseItemKind.MusicArtist },
            { "アーティスト", BaseItemKind.MusicArtist },

            { "albums", BaseItemKind.MusicAlbum },
            { "dischi", BaseItemKind.MusicAlbum },
            { "alben", BaseItemKind.MusicAlbum },
            { "álbumes", BaseItemKind.MusicAlbum },
            { "álbuns", BaseItemKind.MusicAlbum },
            { "ألبومات", BaseItemKind.MusicAlbum },
            { "एल्बम", BaseItemKind.MusicAlbum },
            { "アルバム", BaseItemKind.MusicAlbum },

            { "movies", BaseItemKind.Movie },
            { "film", BaseItemKind.Movie },
            { "video", BaseItemKind.Movie },
            { "filme", BaseItemKind.Movie },
            { "filmes", BaseItemKind.Movie },
            { "films", BaseItemKind.Movie },
            { "películas", BaseItemKind.Movie },
            { "أفلام", BaseItemKind.Movie },
            { "फिल्में", BaseItemKind.Movie },
            { "映画", BaseItemKind.Movie },

            { "songs", BaseItemKind.Audio },
            { "musica", BaseItemKind.Audio },
            { "brani", BaseItemKind.Audio },
            { "canzoni", BaseItemKind.Audio },
            { "canciones", BaseItemKind.Audio },
            { "chansons", BaseItemKind.Audio },
            { "lieder", BaseItemKind.Audio },
            { "nummers", BaseItemKind.Audio },
            { "músicas", BaseItemKind.Audio },
            { "أغانٍ", BaseItemKind.Audio },
            { "गाने", BaseItemKind.Audio },
            { "曲", BaseItemKind.Audio },

            { "series", BaseItemKind.Series },
            { "serie", BaseItemKind.Series },
            { "cartoni", BaseItemKind.Series },
            { "serien", BaseItemKind.Series },
            { "séries", BaseItemKind.Series },
            { "مسلسلات", BaseItemKind.Series },
            { "सीरीज़", BaseItemKind.Series },
            { "シリーズ", BaseItemKind.Series },

            { "books", BaseItemKind.AudioBook },
            { "libri", BaseItemKind.AudioBook },
            { "libros", BaseItemKind.AudioBook },
            { "livres", BaseItemKind.AudioBook },
            { "bücher", BaseItemKind.AudioBook },
            { "boeken", BaseItemKind.AudioBook },
            { "livros", BaseItemKind.AudioBook },
            { "كتب", BaseItemKind.AudioBook },
            { "किताबें", BaseItemKind.AudioBook },
            { "本", BaseItemKind.AudioBook },

            // null = genre or unsupported (playlist) — disambiguate with IsGenreCategory
            { "genres", null },
            { "generi", null },
            { "géneros", null },
            { "gêneros", null },
            { "أنواع", null },
            { "शैलियाँ", null },
            { "ジャンル", null },
            { "playlist", null },
        }.ToFrozenDictionary();

    public static bool IsGenreCategory(string value) =>
        BrowseCategoryToItemKind.TryGetValue(value, out var kind) && kind is null && !string.Equals(value, "playlist", StringComparison.Ordinal);

    public static FrozenDictionary<string, BaseItemKind[]?> MediaTypeToItemKinds { get; } =
        new Dictionary<string, BaseItemKind[]?>
        {
            { "video", VideoKinds },
            { "film", VideoKinds },
            { "filme", VideoKinds },
            { "vídeo", VideoKinds },
            { "vidéo", VideoKinds },
            { "vídeos", VideoKinds },
            { "فيديو", VideoKinds },
            { "वीडियो", VideoKinds },
            { "ビデオ", VideoKinds },

            { "audio", AudioKinds },
            { "musica", AudioKinds },
            { "áudio", AudioKinds },
            { "صوت", AudioKinds },
            { "ऑडियो", AudioKinds },
            { "オーディオ", AudioKinds },

            { "media", null },
            { "medien", null },
            { "contenidos", null },
            { "média", null },
            { "mídia", null },
            { "وسائط", null },
            { "मीडिया", null },
            { "メディア", null },
        }.ToFrozenDictionary();

    public static FrozenDictionary<string, bool> LibraryQueryTypeIsAlbum { get; } =
        new Dictionary<string, bool>
        {
            { "albums", true },
            { "alben", true },
            { "album", true },
            { "álbumes", true },
            { "álbuns", true },
            { "ألبومات", true },
            { "एल्बम", true },
            { "アルバム", true },

            { "tracks", false },
            { "titel", false },
            { "brani", false },
            { "canciones", false },
            { "chansons", false },
            { "faixas", false },
            { "أغانٍ", false },
            { "ट्रैक", false },
            { "トラック", false },
        }.ToFrozenDictionary();
}

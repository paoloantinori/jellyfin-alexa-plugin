using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using SlotMappings = Jellyfin.Plugin.AlexaSkill.Alexa.SlotMappings;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class SlotMappingsTests
{
    [Fact]
    public void BrowseCategory_AllCanonicalValues_AreMapped()
    {
        var allCanonicalValues = ExtractSlotCanonicalValues("BrowseCategory");
        var unmapped = allCanonicalValues
            .Where(v => !SlotMappings.BrowseCategoryToItemKind.ContainsKey(v.ToLowerInvariant()))
            .ToList();

        Assert.Empty(unmapped);
    }

    [Fact]
    public void MediaType_AllCanonicalValues_AreMapped()
    {
        var allCanonicalValues = ExtractSlotCanonicalValues("MediaType");
        var unmapped = allCanonicalValues
            .Where(v => !SlotMappings.MediaTypeToItemKinds.ContainsKey(v.ToLowerInvariant()))
            .ToList();

        Assert.Empty(unmapped);
    }

    [Fact]
    public void LibraryQueryType_AllCanonicalValues_AreMapped()
    {
        var allCanonicalValues = ExtractSlotCanonicalValues("LibraryQueryType");
        var unmapped = allCanonicalValues
            .Where(v => !SlotMappings.LibraryQueryTypeIsAlbum.ContainsKey(v.ToLowerInvariant()))
            .ToList();

        Assert.Empty(unmapped);
    }

    [Fact]
    public void BrowseCategory_GenreValues_AreDetected()
    {
        Assert.True(SlotMappings.IsGenreCategory("genres"));
        Assert.True(SlotMappings.IsGenreCategory("generi"));
        Assert.True(SlotMappings.IsGenreCategory("géneros"));
        Assert.True(SlotMappings.IsGenreCategory("gêneros"));
        Assert.False(SlotMappings.IsGenreCategory("artists"));
        Assert.False(SlotMappings.IsGenreCategory("books"));
    }

    [Fact]
    public void BrowseCategory_Playlist_MapsToNull()
    {
        Assert.True(SlotMappings.BrowseCategoryToItemKind.TryGetValue("playlist", out var kind));
        Assert.Null(kind);
    }

    [Fact]
    public void BrowseCategory_KnownMappings_AreCorrect()
    {
        Assert.Equal(BaseItemKind.MusicArtist, SlotMappings.BrowseCategoryToItemKind["artists"]);
        Assert.Equal(BaseItemKind.MusicAlbum, SlotMappings.BrowseCategoryToItemKind["albums"]);
        Assert.Equal(BaseItemKind.Movie, SlotMappings.BrowseCategoryToItemKind["movies"]);
        Assert.Equal(BaseItemKind.Audio, SlotMappings.BrowseCategoryToItemKind["songs"]);
        Assert.Equal(BaseItemKind.Series, SlotMappings.BrowseCategoryToItemKind["series"]);
        Assert.Equal(BaseItemKind.AudioBook, SlotMappings.BrowseCategoryToItemKind["books"]);
    }

    [Fact]
    public void BrowseCategory_NormalizedCaseLookup()
    {
        // Dictionary keys are lowercase; callers normalize via ToLowerInvariant()
        Assert.True(SlotMappings.BrowseCategoryToItemKind.TryGetValue("artists".ToLowerInvariant(), out var kind));
        Assert.Equal(BaseItemKind.MusicArtist, kind);

        Assert.True(SlotMappings.BrowseCategoryToItemKind.TryGetValue("Artists".ToLowerInvariant(), out var kind2));
        Assert.Equal(BaseItemKind.MusicArtist, kind2);
    }

    [Fact]
    public void BrowseCategory_LocaleCanonicalValues()
    {
        // Italian
        Assert.Equal(BaseItemKind.MusicArtist, SlotMappings.BrowseCategoryToItemKind["artisti"]);
        Assert.Equal(BaseItemKind.Movie, SlotMappings.BrowseCategoryToItemKind["film"]);
        Assert.Equal(BaseItemKind.Series, SlotMappings.BrowseCategoryToItemKind["serie"]);
        Assert.Equal(BaseItemKind.AudioBook, SlotMappings.BrowseCategoryToItemKind["libri"]);

        // German
        Assert.Equal(BaseItemKind.MusicArtist, SlotMappings.BrowseCategoryToItemKind["künstler"]);
        Assert.Equal(BaseItemKind.Movie, SlotMappings.BrowseCategoryToItemKind["filme"]);
        Assert.Equal(BaseItemKind.AudioBook, SlotMappings.BrowseCategoryToItemKind["bücher"]);

        // Japanese
        Assert.Equal(BaseItemKind.Movie, SlotMappings.BrowseCategoryToItemKind["映画"]);
        Assert.Equal(BaseItemKind.AudioBook, SlotMappings.BrowseCategoryToItemKind["本"]);
    }

    private static HashSet<string> ExtractSlotCanonicalValues(string slotTypeName)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in Util.GetLocalInteractionModels())
        {
            var assembly = typeof(Util).Assembly;
            using var stream = assembly.GetManifestResourceStream(model.Item2)!;
            var json = new System.IO.StreamReader(stream).ReadToEnd();
            var root = JObject.Parse(json);
            var languageModel = (JObject)root["languageModel"]!;

            foreach (var type in languageModel["types"]!)
            {
                if (string.Equals(type["name"]?.ToString(), slotTypeName, StringComparison.Ordinal))
                {
                    foreach (var valueEntry in type["values"]!)
                    {
                        var canonical = valueEntry["name"]?["value"]?.ToString();
                        if (canonical != null)
                        {
                            values.Add(canonical);
                        }
                    }
                }
            }
        }

        return values;
    }
}

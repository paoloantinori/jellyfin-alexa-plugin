using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Newtonsoft.Json.Linq;
using Xunit;
using CatalogSlotTypes = Jellyfin.Plugin.AlexaSkill.Alexa.Catalog.CatalogSlotTypes;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Pins the JF-415 musician slot architecture: the 5 en-* locales and it-IT
/// declare the musician slot as the catalog-backed JellyfinArtist custom type
/// (the en-* fix for the AMAZON.Musician knowledge-graph canonicalization that
/// rewrote "queen" to "Paula Abdul"; the it-IT anchor for in-library non-KG
/// artists, JF-508 part A), while every other locale keeps the built-in.
/// Guards four invariants:
/// (1) the swap is atomic per locale: every intent declaring a musician slot
///     uses the same type, and the dialog model agrees (a mismatch makes SMAPI
///     reject the build with MismatchedSlotType, the JF-332 lesson);
/// (2) JellyfinArtist is declared with at least one value where it is used
///     (SMAPI rejects empty custom types);
/// (3) JellyfinArtist is declared NOWHERE else: on unswapped locales the
///     dynamic-entity values would target a type no slot reads;
/// (4) the C# locale set (<see cref="CatalogSlotTypes.CatalogBackedMusicianLocales"/>,
///     which drives the Dialog.UpdateDynamicEntities runtime target) agrees
///     with the models in both directions.
/// </summary>
public class MusicianSlotTypeTests
{
    private const string CatalogBackedType = "JellyfinArtist";
    private const string BuiltInType = "AMAZON.Musician";

    public static IEnumerable<object[]> AllLocales()
    {
        foreach (var model in Util.GetLocalInteractionModels())
        {
            yield return new object[] { model.Item1, model.Item2 };
        }
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void MusicianSlotType_MatchesLocaleArchitecture(string locale, string resourcePath)
    {
        bool catalogBacked = CatalogSlotTypes.CatalogBackedMusicianLocales.Contains(locale);
        string expected = catalogBacked ? CatalogBackedType : BuiltInType;

        JObject root = LoadModel(resourcePath);
        JObject languageModel = (JObject)root["languageModel"]!;

        // (1) every intent declaring a musician slot uses the locale's type
        var slotTypes = new HashSet<string>(StringComparer.Ordinal);
        int musicianSlotIntents = 0;
        foreach (var intent in languageModel["intents"]!)
        {
            foreach (var slot in intent["slots"] ?? Enumerable.Empty<JToken>())
            {
                if (string.Equals(slot["name"]?.ToString(), "musician", StringComparison.Ordinal))
                {
                    musicianSlotIntents++;
                    slotTypes.Add(slot["type"]!.ToString());
                }
            }
        }

        Assert.True(musicianSlotIntents >= 7,
            $"locale {locale}: expected at least 7 intents declaring a musician slot, found {musicianSlotIntents}");
        Assert.True(slotTypes.Count == 1,
            $"locale {locale}: musician slot type is not uniform across intents: {string.Join(", ", slotTypes)}");
        Assert.Equal(expected, slotTypes.Single());

        // (1b) the dialog model agrees with the language model
        foreach (var dialogIntent in root["dialog"]?["intents"] ?? Enumerable.Empty<JToken>())
        {
            foreach (var slot in dialogIntent["slots"] ?? Enumerable.Empty<JToken>())
            {
                if (string.Equals(slot["name"]?.ToString(), "musician", StringComparison.Ordinal))
                {
                    Assert.True(string.Equals(slot["type"]?.ToString(), expected, StringComparison.Ordinal),
                        $"locale {locale}: dialog slot {dialogIntent["name"]}.musician is '{slot["type"]}', expected '{expected}'");
                }
            }
        }

        // (2) + (3) JellyfinArtist declared (with values) exactly where used
        var jellyfinArtist = languageModel["types"]!
            .FirstOrDefault(t => string.Equals(t["name"]?.ToString(), CatalogBackedType, StringComparison.Ordinal));
        if (catalogBacked)
        {
            Assert.NotNull(jellyfinArtist);
            Assert.NotEmpty(jellyfinArtist!["values"]!);
        }
        else
        {
            Assert.Null(jellyfinArtist);
        }
    }

    [Fact]
    public void CatalogBackedMusicianLocales_AgreeWithSwappedModels_BothDirections()
    {
        // The resolver derives from this same set, so per-locale asserts would be
        // true by construction; pin the two dictionary constants it reads instead.
        Assert.Equal("JellyfinArtist", CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Artist]);
        Assert.Equal("AMAZON.Musician", CatalogSlotTypes.Names[CatalogType.Artist]);

        // Every locale NOT in the set resolves to the built-in, and
        // no model outside the set declares JellyfinArtist (an inert-type smell).
        var swappedInModels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in Util.GetLocalInteractionModels())
        {
            JObject root = LoadModel(model.Item2);
            bool usesJellyfinArtist = root["languageModel"]!["intents"]!
                .Any(i => (i["slots"] ?? Enumerable.Empty<JToken>()).Any(s =>
                    string.Equals(s["name"]?.ToString(), "musician", StringComparison.Ordinal)
                    && string.Equals(s["type"]?.ToString(), CatalogBackedType, StringComparison.Ordinal)));
            if (usesJellyfinArtist)
            {
                swappedInModels.Add(model.Item1);
            }

            if (!CatalogSlotTypes.CatalogBackedMusicianLocales.Contains(model.Item1))
            {
                Assert.Equal(BuiltInType, CatalogSlotTypes.ResolveMusicianSlotType(model.Item1));
            }
        }

        Assert.True(swappedInModels.SetEquals(CatalogSlotTypes.CatalogBackedMusicianLocales),
            $"C# locale set and swapped models disagree: models={string.Join(",", swappedInModels.OrderBy(l => l))} " +
            $"csharp={string.Join(",", CatalogSlotTypes.CatalogBackedMusicianLocales.OrderBy(l => l))}");
    }

    private static JObject LoadModel(string resourcePath)
    {
        var assembly = typeof(Util).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourcePath)!;
        return JObject.Parse(new StreamReader(stream).ReadToEnd());
    }
}

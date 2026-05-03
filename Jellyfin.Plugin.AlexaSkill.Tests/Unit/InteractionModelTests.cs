using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class InteractionModelTests
{
    private static readonly string[] CustomIntentsWithSlots =
        ["PlayFavoritesIntent", "PlayLastAddedIntent", "PlaySongIntent", "PlayAlbumIntent",
         "PlayArtistSongsIntent", "PlayPlaylistIntent", "PlayChannelIntent"];

    private static readonly string[] SingleSampleIntents =
        ["PlayVideoIntent"];

    private static readonly string[] CustomIntentsWithoutSlots =
        ["MarkFavoriteIntent", "UnmarkFavoriteIntent", "MediaInfoIntent",
         "LoopSongOnIntent", "RepeatSingleOnIntent", "LoopAllOnIntent", "LoopAllOffIntent"];

    public static IEnumerable<object[]> AllLocales()
    {
        foreach (var model in Util.GetLocalInteractionModels())
        {
            yield return new object[] { model.Item1, model.Item2 };
        }
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void CustomIntentsWithSlots_HaveMinimumSamples(string locale, string resourcePath)
    {
        var (languageModel, intentSamples) = LoadIntentSamples(resourcePath);

        foreach (var intentName in CustomIntentsWithSlots)
        {
            if (intentSamples.TryGetValue(intentName, out var samples))
            {
                Assert.True(samples.Count >= 2,
                    $"Locale {locale}: {intentName} should have at least 2 sample utterances, found {samples.Count}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void SingleSampleIntents_HaveAtLeastOneSample(string locale, string resourcePath)
    {
        var (languageModel, intentSamples) = LoadIntentSamples(resourcePath);

        foreach (var intentName in SingleSampleIntents)
        {
            if (intentSamples.TryGetValue(intentName, out var samples))
            {
                Assert.True(samples.Count >= 1,
                    $"Locale {locale}: {intentName} should have at least 1 sample utterance, found {samples.Count}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void CustomIntentsWithoutSlots_HaveMinimumSamples(string locale, string resourcePath)
    {
        var (languageModel, intentSamples) = LoadIntentSamples(resourcePath);

        foreach (var intentName in CustomIntentsWithoutSlots)
        {
            if (intentSamples.TryGetValue(intentName, out var samples))
            {
                Assert.True(samples.Count >= 2,
                    $"Locale {locale}: {intentName} should have at least 2 sample utterances, found {samples.Count}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void AllSamples_AreNonEmpty(string locale, string resourcePath)
    {
        var (languageModel, intentSamples) = LoadIntentSamples(resourcePath);

        foreach (var (intentName, samples) in intentSamples)
        {
            foreach (var sample in samples)
            {
                Assert.False(string.IsNullOrWhiteSpace(sample),
                    $"Locale {locale}: {intentName} has an empty sample utterance");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void PlayFavoritesIntent_HasUtteranceWithoutSlot(string locale, string resourcePath)
    {
        Assert.False(string.IsNullOrEmpty(locale));
        var (languageModel, intentSamples) = LoadIntentSamples(resourcePath);

        if (intentSamples.TryGetValue("PlayFavoritesIntent", out var samples))
        {
            Assert.Contains(samples, s => !s.Contains("{media_type}", StringComparison.Ordinal));
        }
    }

    private static (JObject LanguageModel, Dictionary<string, List<string>> IntentSamples) LoadIntentSamples(string resourcePath)
    {
        var assembly = typeof(Util).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourcePath)!;
        var reader = new System.IO.StreamReader(stream);
        var json = reader.ReadToEnd();
        var root = JObject.Parse(json);
        var languageModel = (JObject)root["languageModel"]!;

        var intentSamples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var intent in languageModel["intents"]!)
        {
            var name = intent["name"]!.ToString();
            if (name.StartsWith("AMAZON.", StringComparison.Ordinal))
            {
                continue;
            }

            var samples = intent["samples"]?
                .Select(s => s.ToString())
                .ToList() ?? new List<string>();

            intentSamples[name] = samples;
        }

        return (languageModel, intentSamples);
    }
}

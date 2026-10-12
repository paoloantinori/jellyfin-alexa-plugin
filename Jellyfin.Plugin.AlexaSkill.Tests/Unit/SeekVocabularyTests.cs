using System.Collections.Generic;
using System.IO;
using System.Linq;
using Alexa.NET.Request;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Mirror guard (the ItalianNumberWordsTests JF-451 pattern) for the
/// SeekVocabulary raw word sets: every direction and unit word declared in any
/// locale's embedded SeekDirection / SeekUnit slot type (value plus synonyms)
/// must resolve through the raw branch, so a word added to a template without
/// its SeekVocabulary twin fails here instead of silently degrading to the
/// forward / seconds defaults at runtime (the wrong-way class JF-875 closed).
/// The forward and seconds sides pin the negative: their words must not read
/// as back / minutes. Rows are derived from the embedded models, so locale 18
/// joins the guard with its template alone.
/// </summary>
public class SeekVocabularyTests
{
    public static IEnumerable<object[]> BackWordsFromModels => WordsFromTypes("SeekDirection", "back");

    public static IEnumerable<object[]> MinuteWordsFromModels => WordsFromTypes("SeekUnit", "minutes");

    public static IEnumerable<object[]> ForwardWordsFromModels => WordsFromTypes("SeekDirection", "forward");

    public static IEnumerable<object[]> SecondWordsFromModels => WordsFromTypes("SeekUnit", "seconds");

    [Theory]
    [MemberData(nameof(BackWordsFromModels))]
    public void EveryBackDirectionWord_ResolvesBackward(string word)
        => Assert.True(SeekVocabulary.IsBackward(RawSlot(word)), $"back word '{word}' does not resolve backward");

    [Theory]
    [MemberData(nameof(MinuteWordsFromModels))]
    public void EveryMinuteUnitWord_ResolvesMinutes(string word)
        => Assert.True(SeekVocabulary.IsMinutes(RawSlot(word)), $"minute word '{word}' does not resolve as minutes");

    [Theory]
    [MemberData(nameof(ForwardWordsFromModels))]
    public void EveryForwardDirectionWord_StaysForward(string word)
        => Assert.False(SeekVocabulary.IsBackward(RawSlot(word)), $"forward word '{word}' must not resolve backward");

    [Theory]
    [MemberData(nameof(SecondWordsFromModels))]
    public void EverySecondUnitWord_StaysSeconds(string word)
        => Assert.False(SeekVocabulary.IsMinutes(RawSlot(word)), $"seconds word '{word}' must not resolve as minutes");

    /// <summary>A slot carrying only the raw spoken word, the no-ER delivery shape.</summary>
    private static Slot RawSlot(string word) => new() { Name = "seek_slot", Value = word };

    /// <summary>
    /// The flat cross-locale union of the value plus synonyms of every entry
    /// carrying the given id in the given slot type, read from the embedded
    /// interaction models (the unwrapped resource shape, the authoritative
    /// source the SeekVocabulary doc comment names).
    /// </summary>
    private static IEnumerable<object[]> WordsFromTypes(string typeName, string id)
    {
        // Deduped: locale families share words ("segundos" on es-ES/MX/US and
        // pt-BR), and duplicate theory rows collide on xUnit's test id and
        // skip noisily.
        var words = new HashSet<string>();
        var assembly = typeof(global::Jellyfin.Plugin.AlexaSkill.Util).Assembly;
        foreach (var model in TestLocales.Models())
        {
            using var stream = assembly.GetManifestResourceStream(model.Item2);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var root = JObject.Parse(reader.ReadToEnd());

            var entries = root["languageModel"]!["types"]!
                .OfType<JObject>()
                .Where(t => (string?)t["name"] == typeName)
                .SelectMany(t => t["values"]!.OfType<JObject>())
                .Where(v => (string?)v["id"] == id);

            foreach (var entry in entries)
            {
                words.Add((string)entry["name"]!["value"]!);
                foreach (var synonym in entry["name"]!["synonyms"]?.OfType<JValue>() ?? Enumerable.Empty<JValue>())
                {
                    words.Add((string)synonym!);
                }
            }
        }

        return words.Select(word => new object[] { word });
    }
}

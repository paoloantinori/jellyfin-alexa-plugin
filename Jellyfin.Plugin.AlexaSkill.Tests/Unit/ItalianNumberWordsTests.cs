using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// The ItalianNumberWords table must mirror the ItalianNumber slot values exactly
/// (JF-451): every word Alexa can return for duration_minutes must parse, and digit
/// strings (the AMAZON.NUMBER behavior in the other 16 locales) keep parsing.
/// </summary>
public class ItalianNumberWordsTests
{
    [Theory]
    [InlineData("trenta", 30)]
    [InlineData("Venti", 20)]
    [InlineData("novanta", 90)]
    [InlineData("cento", 100)]
    [InlineData("tre", 3)]
    [InlineData("quindici", 15)]
    [InlineData("30", 30)]
    public void TryParse_KnownWordsAndDigits_Parse(string text, int expected)
    {
        Assert.True(ItalianNumberWords.TryParse(text, out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("trentaquattro")] // Compound, not in the ItalianNumber value set.
    [InlineData("ciao")]
    public void TryParse_UnknownText_ReturnsFalse(string? text)
    {
        Assert.False(ItalianNumberWords.TryParse(text, out _));
    }

    /// <summary>
    /// Mirror guard against two sources of truth drifting: every value declared in
    /// the embedded it-IT model's ItalianNumber type must parse, so a word added to
    /// the slot type without a parser entry fails here instead of silently degrading
    /// to the did-not-catch prompt at runtime.
    /// </summary>
    [Fact]
    public void EveryItalianNumberSlotValue_Parses()
    {
        var assembly = typeof(global::Jellyfin.Plugin.AlexaSkill.Util).Assembly;
        var model = global::Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels()
            .First(m => m.Item1 == "it-IT");

        using var stream = assembly.GetManifestResourceStream(model.Item2);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var root = JObject.Parse(reader.ReadToEnd());

        string[] slotValues = root["languageModel"]!["types"]!
            .OfType<JObject>()
            .Single(t => (string?)t["name"] == "ItalianNumber")["values"]!
            .Select(v => (string)v["name"]!["value"]!)
            .ToArray();

        Assert.NotEmpty(slotValues);
        foreach (string word in slotValues)
        {
            Assert.True(ItalianNumberWords.TryParse(word, out _),
                $"ItalianNumber slot value '{word}' does not parse via ItalianNumberWords");
        }
    }
}

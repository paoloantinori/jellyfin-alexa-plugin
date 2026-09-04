using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Invariants over locale response strings that the python validators cannot express
/// (they check key coverage, not string shape). JF-487 locks the welcome join and the
/// FindSong count grammar.
/// </summary>
public class LocaleStringsTests
{
    private static readonly string[] AllLocales =
    {
        "ar-SA", "de-DE", "en-AU", "en-CA", "en-GB", "en-IN", "en-US",
        "es-ES", "es-MX", "es-US", "fr-CA", "fr-FR", "hi-IN", "it-IT",
        "ja-JP", "nl-NL", "pt-BR"
    };

    /// <summary>
    /// Separator characters that survive SSML tag-stripping in the Alexa app's
    /// speech transcript: Western comma/period/exclamation, Arabic comma, Japanese
    /// period and full-width exclamation. Scripts without spaces (ja) rely on these.
    /// </summary>
    private static readonly HashSet<char> VisibleSeparators = new(",.!。、،！？！?");

    [Theory]
    [MemberData(nameof(AllLocalesData))]
    public void WelcomeSsml_JoinCarriesVisibleSeparator(string locale)
    {
        // JF-487 defect 4 (device 2026-09-04: "Benvenuto in Jellyfin SkillCosa posso
        // riprodurre?"): the greeting and the follow-up question were joined by ONLY a
        // <break> tag; when the app strips the tag for display the two halves glue
        // together. The char immediately BEFORE the break must be a separator that
        // survives stripping.
        AssertSeparatorBeforeBreak(ResponseStrings.Get("WelcomeSsml", locale), "WelcomeSsml", locale);
        AssertSeparatorBeforeBreak(ResponseStrings.Get("WelcomePersonalizedSsml", locale), "WelcomePersonalizedSsml", locale);
    }

    [Theory]
    [MemberData(nameof(AllLocalesData))]
    public void FindSongFoundMultipleSingular_ExistsWithCountArg(string locale)
    {
        // JF-487 defect 3: the one-candidate prompt needs a grammatical-singular
        // variant ("1 canzone", "1 song"); the key must resolve (not fall back to the
        // key name) and keep the {0} count format arg.
        string singular = ResponseStrings.Get("FindSongFoundMultipleSingular", locale);
        Assert.NotEqual("FindSongFoundMultipleSingular", singular);
        Assert.Contains("{0}", singular);
    }

    [Fact]
    public void FindSongCountGrammar_ItItalian_Inflects()
    {
        // it-IT (the device locale) genuinely inflects: singular "canzone", plural
        // "canzoni". The device spoke "1 canzoni".
        Assert.Contains("canzone", ResponseStrings.Get("FindSongFoundMultipleSingular", "it-IT"));
        Assert.Contains("canzoni", ResponseStrings.Get("FindSongFoundMultiple", "it-IT"));
    }

    [Fact]
    public void FindSongCountGrammar_EnUS_Inflects()
    {
        Assert.Contains("song", ResponseStrings.Get("FindSongFoundMultipleSingular", "en-US"));
        Assert.Contains("songs", ResponseStrings.Get("FindSongFoundMultiple", "en-US"));
    }

    public static TheoryData<string> AllLocalesData()
    {
        var data = new TheoryData<string>();
        foreach (string locale in AllLocales)
        {
            data.Add(locale);
        }

        return data;
    }

    private static void AssertSeparatorBeforeBreak(string ssml, string key, string locale)
    {
        int breakIndex = ssml.IndexOf("<break", StringComparison.Ordinal);
        Assert.True(breakIndex > 0, $"{key} [{locale}] must contain a <break> join");

        // Walk back over whitespace: the separator may sit one space before the tag
        // ("Skill, <break/>"); what must NOT happen is a letter/digit directly joined
        // to the tag ("Skill<break/>"), which strips to "SkillCosa...".
        int i = breakIndex - 1;
        while (i >= 0 && char.IsWhiteSpace(ssml[i]))
        {
            i--;
        }

        Assert.True(i >= 0 && VisibleSeparators.Contains(ssml[i]),
            $"{key} [{locale}] must carry a visible separator (comma/period/exclamation) before the <break> tag; found '{(i >= 0 ? ssml[i] : '?')}'");
    }
}

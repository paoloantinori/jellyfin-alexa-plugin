using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class PhoneticSynonymGeneratorTests
{
    // --- Italian locale tests (existing behavior preserved) ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void GenerateSynonyms_ShortOrNullNames_ReturnEmpty(string? name)
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms(name!, "it-IT");
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Metallica")]
    [InlineData("Adele")]
    [InlineData("Pausini")]
    [InlineData("Bocelli")]
    [InlineData("Pavarotti")]
    public void GenerateSynonyms_ItalianOriginNames_ReturnEmpty(string name)
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms(name, "it-IT");
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Brunello")]
    [InlineData("Bianchetti")]
    [InlineData("Moretti")]
    public void GenerateSynonyms_ItalianEndings_ReturnEmpty(string name)
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms(name, "it-IT");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_Queen_ProducesPhoneticVariant()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Queen", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains('u') || s.Contains('v'));
    }

    [Fact]
    public void GenerateSynonyms_TheBeatles_StripsArticleAndAddsItalianVariant()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Beatles", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.StartsWith("i "));
    }

    [Fact]
    public void GenerateSynonyms_PinkFloyd_TransformsPhToF()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Floid") || !s.Contains("ph"));
    }

    [Fact]
    public void GenerateSynonyms_Backstreet_Boys_TransformsCkToK()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Backstreet Boys", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ck"));
    }

    [Fact]
    public void GenerateSynonyms_WithTh_TransformsToT()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Smiths", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("th"));
    }

    [Fact]
    public void GenerateSynonyms_ReturnsMaxFiveSynonyms()
    {
        // JF-362 raised the per-name synonym cap from 3 to 5 to offer more ASR-coverage
        // variants. Use an input that actually exercises the cap (multiple transformable
        // features -> 5 variants).
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Motion Orchestra", "it-IT");
        Assert.True(result.Count <= 5, $"Expected <= 5 synonyms, got {result.Count}");
    }

    [Fact]
    public void GenerateSynonyms_NoDuplicatesInResult()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Weather Underground", "it-IT");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result);
    }

    [Fact]
    public void GenerateSynonyms_LeadingH_DroppedInVariant()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Heart", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.StartsWith("H") || s == "Heart");
    }

    [Fact]
    public void GenerateSynonyms_WithW_TransformsW()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("White Stripes", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains('v') || s.Contains('V') || s.Contains('u') || s.Contains('U'));
    }

    [Fact]
    public void GenerateSynonyms_OughSound_TransformsToOf()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Through Fire", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => !s.Contains("ough"));
    }

    [Fact]
    public void GenerateSynonyms_TionSuffix_TransformsToSionOrZion()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Motion Orchestra", "it-IT");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("sion") || s.Contains("zion"));
    }

    [Fact]
    public void GenerateSynonyms_NoTransformablePhonetics_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Nirvana", "it-IT");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_BandWithTransformableFeatures_ProducesVariants()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Weather Report", "it-IT");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GenerateSynonyms_DoubledConsonantName_DetectedAsItalian()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Botticelli", "it-IT");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_WithSh_TransformsShToSc()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Fleetwood Mac", "it-IT");
        Assert.NotEmpty(result);
    }

    // --- Locale dispatch tests ---

    [Fact]
    public void GenerateSynonyms_EnglishLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "en-US");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_EnglishGBLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Beatles", "en-GB");
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_ItalianLocaleVariant_Works()
    {
        // it-IT and it-CH should both dispatch to Italian phonetics
        var resultIT = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "it-IT");
        var resultCH = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "it-CH");
        Assert.NotEmpty(resultIT);
        Assert.NotEmpty(resultCH);
        Assert.Equal(resultIT.Count, resultCH.Count);
    }

    [Fact]
    public void GenerateSynonyms_UnknownLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "xx-XX");
        Assert.Empty(result);
    }

    // --- Portuguese locale dispatch tests ---

    [Fact]
    public void GenerateSynonyms_PortugueseBR_DispatchesToPortuguese()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "pt-BR");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smids") && !s.Contains("th"));
    }

    [Fact]
    public void GenerateSynonyms_PortuguesePT_DispatchesToPortuguese()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "pt-PT");
        Assert.NotEmpty(result);
    }

    // --- Japanese locale dispatch tests ---

    [Fact]
    public void GenerateSynonyms_JapaneseJP_DispatchesToJapanese()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "ja-JP");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smis") && !s.Contains("th"));
    }

    [Fact]
    public void GenerateSynonyms_JapaneseJP_LToRTransform()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Linkin Park", "ja-JP");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Rinkin"));
    }

    // --- Dutch locale dispatch tests ---

    [Fact]
    public void GenerateSynonyms_DutchNL_DispatchesToDutch()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "nl-NL");
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Smits") && !s.Contains("th"));
    }

    [Fact]
    public void GenerateSynonyms_DutchBE_DispatchesToDutch()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("The Smiths", "nl-BE");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GenerateSynonyms_NullLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", null!);
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSynonyms_EmptyLocale_ReturnsEmpty()
    {
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Pink Floyd", "");
        Assert.Empty(result);
    }

    // --- JF-362: Romance L1 pronunciation of English ---
    // Italian/Spanish/French/Portuguese L1 speakers all lack the velar nasal /ŋ/ as a
    // phoneme, so they realize English "-ing" as /in/. (German and Dutch are Germanic and
    // DO have /ŋ/, so they are excluded — see the de-DE test below.) On-device ASR captured
    // an Italian saying "Soul Coughing" as "sol coffin"; the catalog slot must emit that
    // spoken form. See claudedocs/research_jf362-italian-phonetic-synonyms_2026-07-22.md.

    private static readonly string[] NgAbsentLocales_JF362 = { "it-IT", "es-ES", "fr-FR", "pt-BR" };

    [Theory]
    [MemberData(nameof(NgAbsentLocalesTheoryData_JF362))]
    public void GenerateSynonyms_IngEnding_TransformsToIn_AcrossNgAbsentLocales_JF362(string locale)
    {
        // it/es/fr/pt L1 lacks /ŋ/ -> "-ing" surfaces as a dropped-g form. The exact
        // spelling depends on whether the locale also has ough->of: it-IT chains to
        // "Cofin" (pinned separately), the others give "Coughin".
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Coughing", locale);
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("Cofin", StringComparison.OrdinalIgnoreCase)
                                  || s.Contains("Coughin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateSynonyms_IngEnding_ItalianChainsOughToOf_ThenIngToIn_JF362()
    {
        // it-IT is the only one of the four with an ough->of rule, so the chain is
        // Coughing -> Cofing -> Cofin. Lock this locale-specific difference explicitly
        // (the other three produce "Coughin" — covered by the theory above).
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Coughing", "it-IT");
        Assert.Contains(result, s => s.Contains("Cofin", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(NgAbsentLocalesTheoryData_JF362))]
    public void GenerateSynonyms_SoulCoughing_ProducesSpokenForm_AcrossNgAbsentLocales_JF362(string locale)
    {
        // The on-device repro: an Italian says "sol coffin". Each /ŋ/-absent locale must
        // emit a spoken-form synonym: "soul" -> "sol" (the override) AND the second word's
        // terminal -ing dropped to -in.
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Soul Coughing", locale);
        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Contains("sol", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, s => s.Contains("Cofin", StringComparison.OrdinalIgnoreCase)
                                  || s.Contains("Coughin", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("nl-NL")]
    public void GenerateSynonyms_IngEnding_GermanicLocales_DoNotDropIng_JF362(string locale)
    {
        // Guard: German and Dutch have /ŋ/ natively (Ding, singen, zingen), so they must
        // NOT apply the -ing->-in Romance rule. A dropped-g synonym would be a spurious
        // wrong-pronunciation variant. Lock the exclusion so it isn't re-added.
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Coughing", locale);
        Assert.All(result, s => Assert.DoesNotContain("Cofin", s, StringComparison.OrdinalIgnoreCase));
        Assert.All(result, s => Assert.DoesNotContain("Coughin", s, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateSynonyms_BareIng_NotTransformed_LengthGuard_JF362()
    {
        // The word.Length > 3 guard excludes the bare word "ing" (length 3) — it must not
        // become "in". (The generator may return empty for it, which also satisfies this.)
        var result = PhoneticSynonymGenerator.GenerateSynonyms("ing", "it-IT");
        Assert.All(result, s => Assert.DoesNotContain(" in", s, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Sting")]
    [InlineData("King")]
    public void GenerateSynonyms_FourLetterIngWord_Transforms_AcceptedBroadBehavior_JF362(string word)
    {
        // Known over-broad approximation (accepted): 4-letter words ending in "-ing" pass
        // the Length>3 guard even though the -ing is the root, not a suffix. This is
        // linguistically correct for /ŋ/-absent L1 speakers — an Italian genuinely says
        // "Stin"/"Kin". Pinned so a future change to the guard is caught deliberately.
        var result = PhoneticSynonymGenerator.GenerateSynonyms(word, "it-IT");
        Assert.Contains(result, s => s.Contains(word[..^3], StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<string> NgAbsentLocalesTheoryData_JF362 =>
        new() { "it-IT", "es-ES", "fr-FR", "pt-BR" };

    // --- JF-362 (device capture): coverage, not precision ---
    // The goal is to offer ALTERNATIVE spoken-form variants so that whichever way Alexa's
    // ASR transcribes an Italian pronunciation, at least one synonym matches (entity
    // resolution only needs one hit; extra near-misses are harmless). On-device ASR
    // captured "Soul Coughing" as both "sol coffin" and "soul coffin" — single AND double
    // 'f', sol AND soul. The synonym set must cover {sol,soul} × {cofin,coffin}.
    // See claudedocs/research_jf362-italian-gemination-doubling_2026-07-23.md.

    [Theory]
    [MemberData(nameof(NgAbsentLocalesTheoryData_JF362))]
    public void GenerateSynonyms_SoulCoughing_CoversDeviceCaptures_AcrossNgAbsentLocales_JF362(string locale)
    {
        // Device ASR produced "soul coffin" (corr be57f36f) and "sol coffin" (corr 32a6617e).
        // The doubler covers whatever single intervocalic consonant each locale's transform
        // produces. it-IT runs ough->of first, so "Cofin" -> doubled "Coffin" (the device
        // capture). es/fr/pt have no ough->of rule, so they keep "Coughin" (no intervocalic f
        // to double) — that is locally correct, not a gap.
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Soul Coughing", locale);

        // The dropped-g form (Cofin for it-IT, Coughin for es/fr/pt) must be present.
        Assert.Contains(result, s => s.Contains("Cofin", StringComparison.OrdinalIgnoreCase)
                                  || s.Contains("Coughin", StringComparison.OrdinalIgnoreCase));

        if (locale == "it-IT")
        {
            // The device-capture doubled-f form is reachable only via the Italian ough->of chain.
            Assert.Contains(result, s => s.Contains("Coffin", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void GenerateSynonyms_DoubleConsonantVariant_EmittedForIntervocalicConsonant_JF362()
    {
        // A word with a single intervocalic consonant that ASR may double should emit a
        // doubled-consonant variant as an additional synonym (coverage). "Coughing" -> the
        // -ing->-in path yields "Cofin"; a doubled-f variant "Coffin" should also appear.
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Coughing", "it-IT");
        Assert.Contains(result, s => s.Contains("Coffin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateSynonyms_SoulCoughing_CoversBothSolAndSoulDeviceCaptures_JF362()
    {
        // Device ASR captured BOTH "sol coffin" (corr 32a6617e) and "soul coffin" (corr
        // be57f36f). ASR is inconsistent about the override vowel — sometimes keeps "soul".
        // The synonym set must cover the doubled-f form with BOTH vowels (coverage).
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Soul Coughing", "it-IT");
        Assert.Contains(result, s => s.Contains("Sol Coffin", StringComparison.OrdinalIgnoreCase)
                                  || s.Contains("sol coffin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, s => s.Contains("Soul Coffin", StringComparison.OrdinalIgnoreCase)
                                  || s.Contains("soul coffin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateSynonyms_MultiWordOverrideName_KeepsDeviceCaptureUnderCap_JF362()
    {
        // Regression guard (code-review MUST-FIX 1): when an override word ("Soul") and
        // other transformable words co-occur, the synonym list can exceed the Take(5) cap.
        // The device-captured "Soul Coffin" form must survive (added before lower-priority
        // alternates), not be truncated. "Motion Soul Coughing" has tion + override + ough.
        var result = PhoneticSynonymGenerator.GenerateSynonyms("Motion Soul Coughing", "it-IT");
        Assert.Contains(result, s => s.Contains("Soul Coffin", StringComparison.OrdinalIgnoreCase));
    }
}

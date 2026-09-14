using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-379: the c/k/ck/q velar-stop substitution family. Live evidence (2026-07-25,
/// it-IT Echo): the artist "Koop" transcribed as BOTH "cup" and "coop" - Romance-L1
/// ASR renders foreign /k/ unpredictably (c, k, ck, q) and drifts the "oo" vowel.
/// These tests pin the coverage contract: velar-stop names emit the drift family;
/// names with no velar stop emit nothing.
/// </summary>
public class VelarStopVariantTests
{
    // --- The filed incident: Koop must emit at least one ASR-attested form ---

    [Theory]
    [InlineData("Koop")]
    [InlineData("koop")]
    public void Generate_Koop_EmitsAnAttestedAsrForm(string name)
    {
        var result = ItalianPhoneticSynonyms.Generate(name);

        Assert.Contains(result, s => s.Equals("cup", StringComparison.OrdinalIgnoreCase)
                                    || s.Equals("coop", StringComparison.OrdinalIgnoreCase)
                                    || s.Equals("cop", StringComparison.OrdinalIgnoreCase));
    }

    // --- No velar stop: no spurious variants (the AC #3 guard) ---

    [Theory]
    [InlineData("Beatles")]
    [InlineData("Adele")]
    [InlineData("Beyonce")]
    public void Generate_NoVelarStop_NoVariantsAtAll(string name)
    {
        // Velar-free names produce nothing here (no other transform fires for them
        // either), which pins both the AC#3 no-spurious-variants contract and the
        // wiring boundary at the integration level.
        Assert.Empty(ItalianPhoneticSynonyms.Generate(name));
    }

    // --- The shared helper (pure, seam-free) ---

    [Fact]
    public void GetVelarStopVariants_Koop_CoversTheDriftFamily()
    {
        var variants = PhoneticSynonymGenerator.GetVelarStopVariants("Koop");

        Assert.Contains(variants, v => v.Equals("Coop", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(variants, v => v.Equals("Cup", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(variants, v => v.Equals("Cop", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(variants, v => v.Equals("Quop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetVelarStopVariants_NoVelarStop_ReturnsEmpty()
    {
        Assert.Empty(PhoneticSynonymGenerator.GetVelarStopVariants("Beatles"));
        Assert.Empty(PhoneticSynonymGenerator.GetVelarStopVariants("Adele"));
        Assert.Empty(PhoneticSynonymGenerator.GetVelarStopVariants("Beyonce"));
    }

    [Fact]
    public void GetVelarStopVariants_SoftCNotTreatedAsVelar()
    {
        // "Celine" starts with a SOFT c (before e): the c is /s/, not /k/; swapping
        // it to k would be a spurious variant. Only the hard c participates.
        var variants = PhoneticSynonymGenerator.GetVelarStopVariants("Celine");

        Assert.DoesNotContain(variants, v => v.StartsWith("K", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetVelarStopVariants_ChDigraph_IsNotADriftSite()
    {
        // Italian "ch" is the NATIVE /k/ spelling before front vowels ("che"/"chi"),
        // so Italian-origin names like "Bianchi" must not drift (regression pin:
        // the first cut emitted a nonsense "Bianqhi" here).
        Assert.Empty(PhoneticSynonymGenerator.GetVelarStopVariants("Bianchi"));

        // A velar elsewhere in the word still fires, but the ch digraph stays intact.
        Assert.All(PhoneticSynonymGenerator.GetVelarStopVariants("Kitchen"), v => v.Contains("chen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetVelarStopVariants_CkCluster_CollapsesToSingleStops()
    {
        var variants = PhoneticSynonymGenerator.GetVelarStopVariants("Back");

        Assert.Contains(variants, v => v.Equals("Bac", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(variants, v => v.Equals("Bak", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetVelarStopVariants_QuAlreadyPresent_DoesNotDoubleTheU()
    {
        // A q followed by "u" already carries the Italian glide: "Cure" must yield
        // "Qure", never "Quure" (review finding: The Cure emitted "Quure").
        var variants = PhoneticSynonymGenerator.GetVelarStopVariants("Cure");

        Assert.Contains(variants, v => v.Equals("Qure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(variants, v => v.Contains("uu", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetVelarStopVariants_MultiWord_OnlyVelarWordsChange()
    {
        var variants = PhoneticSynonymGenerator.GetVelarStopVariants("Simon Back");

        Assert.Contains(variants, v => v.Equals("Simon Bac", StringComparison.OrdinalIgnoreCase));
        Assert.All(variants, v => Assert.StartsWith("Simon", v, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetVelarStopVariants_BoundedPerWord()
    {
        // A single velar word must not explode: the family is the consonant swaps
        // plus the oo-vowel drifts, bounded.
        var variants = PhoneticSynonymGenerator.GetVelarStopVariants("Koop");

        Assert.InRange(variants.Count, 1, 8);
    }

    [Fact]
    public void GetVelarStopVariants_PreservesLeadingCase()
    {
        var variants = PhoneticSynonymGenerator.GetVelarStopVariants("Koop");

        Assert.All(variants, v => Assert.True(char.IsUpper(v[0]), $"variant '{v}' must preserve the leading capital"));
    }
}

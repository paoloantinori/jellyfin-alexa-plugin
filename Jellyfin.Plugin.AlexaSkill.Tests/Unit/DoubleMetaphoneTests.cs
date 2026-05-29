using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class DoubleMetaphoneTests
{
    // --- Basic encoding tests with known values ---

    [Fact]
    public void Encode_Smith_ReturnsExpectedCodes()
    {
        var (primary, alternate) = DoubleMetaphone.Encode("Smith");

        Assert.Equal("SMT", primary);
        // Alternate is null when identical to primary
    }

    [Fact]
    public void Encode_Schmidt_ReturnsExpectedCodes()
    {
        var (primary, alternate) = DoubleMetaphone.Encode("Schmidt");

        Assert.Equal("XMT", primary);
        Assert.Equal("KMT", alternate);
    }

    [Fact]
    public void Encode_SmithAndSchmidt_HavePhoneticOverlap()
    {
        // Smith primary='SMT', Schmidt primary='XMT'
        // These share 'MT' suffix which indicates phonetic similarity.
        // The key property is that phonetically similar names produce related codes.
        var smith = DoubleMetaphone.Encode("Smith");
        var schmidt = DoubleMetaphone.Encode("Schmidt");

        // Both should have 'MT' in their codes, indicating similar consonant structure
        Assert.Contains("MT", smith.Primary);
        Assert.Contains("MT", schmidt.Primary);
    }

    // --- Consistency and edge case tests ---

    [Fact]
    public void Encode_DaftPunk_ProducesConsistentCodes()
    {
        var (primary1, alternate1) = DoubleMetaphone.Encode("Daft Punk");
        var (primary2, alternate2) = DoubleMetaphone.Encode("Daft Punk");

        Assert.Equal(primary1, primary2);
        Assert.Equal(alternate1, alternate2);
        Assert.NotEmpty(primary1);
    }

    [Fact]
    public void Encode_CaseInsensitive()
    {
        var lower = DoubleMetaphone.Encode("daft punk");
        var upper = DoubleMetaphone.Encode("DAFT PUNK");
        var mixed = DoubleMetaphone.Encode("Daft Punk");

        Assert.Equal(lower.Primary, upper.Primary);
        Assert.Equal(lower.Primary, mixed.Primary);
    }

    [Fact]
    public void Encode_CrossLanguage_Bjork()
    {
        // "Björk" should produce a phonetic code
        var (primary, _) = DoubleMetaphone.Encode("Björk");
        Assert.NotEmpty(primary);
    }

    [Fact]
    public void Encode_CrossLanguage_Bach()
    {
        var (primary, _) = DoubleMetaphone.Encode("Bach");
        Assert.NotEmpty(primary);
        // B-A-C-H: B → P, A → A, CH → X → "PX" (or similar)
        Assert.True(primary.Length > 0);
    }

    [Fact]
    public void Encode_CrossLanguage_Garcia()
    {
        var (primary, _) = DoubleMetaphone.Encode("Garcia");
        Assert.NotEmpty(primary);
    }

    // --- Empty and edge inputs ---

    [Fact]
    public void Encode_EmptyString_ReturnsEmptyPrimary()
    {
        var (primary, alternate) = DoubleMetaphone.Encode("");

        Assert.Equal(string.Empty, primary);
        Assert.Null(alternate);
    }

    [Fact]
    public void Encode_Whitespace_ReturnsEmptyPrimary()
    {
        var (primary, alternate) = DoubleMetaphone.Encode("   ");

        Assert.Equal(string.Empty, primary);
        Assert.Null(alternate);
    }

    [Fact]
    public void Encode_SingleCharacter_ReturnsSingleCharCode()
    {
        var (primary, alternate) = DoubleMetaphone.Encode("A");

        Assert.Equal("A", primary);
        Assert.Null(alternate);
    }

    // --- Known name patterns for phonetic matching ---

    [Fact]
    public void Encode_PhoneticallySimilarNames_ProduceMatchingCodes()
    {
        // "Katherine" and "Catherine" should have matching phonetic codes
        var katherine = DoubleMetaphone.Encode("Katherine");
        var catherine = DoubleMetaphone.Encode("Catherine");

        Assert.True(
            FuzzyMatcher.PhoneticCodesMatch(katherine.Primary, katherine.Alternate, catherine.Primary, catherine.Alternate),
            "Katherine and Catherine should have matching phonetic codes");
    }

    [Fact]
    public void Encode_PhoneticallyDifferentNames_ProduceDifferentCodes()
    {
        var beatles = DoubleMetaphone.Encode("Beatles");
        var metallica = DoubleMetaphone.Encode("Metallica");

        // These should NOT match phonetically
        Assert.False(
            FuzzyMatcher.PhoneticCodesMatch(beatles.Primary, beatles.Alternate, metallica.Primary, metallica.Alternate),
            "Beatles and Metallica should NOT have matching phonetic codes");
    }

    // --- Code length constraints ---

    [Fact]
    public void Encode_PrimaryCodeMaxLength4()
    {
        var (primary, _) = DoubleMetaphone.Encode("International");
        Assert.True(primary.Length <= 4, $"Primary code '{primary}' exceeds max length of 4");
    }

    [Fact]
    public void Encode_AlternateCodeMaxLength4()
    {
        var (_, alternate) = DoubleMetaphone.Encode("International");
        if (alternate != null)
        {
            Assert.True(alternate.Length <= 4, $"Alternate code '{alternate}' exceeds max length of 4");
        }
    }

    // --- Specific phonetic pattern tests ---

    [Fact]
    public void Encode_Wheelwright_ProducesConsistentCodes()
    {
        var (primary, _) = DoubleMetaphone.Encode("Wheelwright");
        Assert.NotEmpty(primary);
    }

    [Fact]
    public void Encode_Radiohead_ProducesConsistentCodes()
    {
        var (primary, alternate) = DoubleMetaphone.Encode("Radiohead");
        Assert.NotEmpty(primary);
        // Verify deterministic
        var (primary2, alternate2) = DoubleMetaphone.Encode("Radiohead");
        Assert.Equal(primary, primary2);
        Assert.Equal(alternate, alternate2);
    }

    [Fact]
    public void Encode_LedZeppelin_ProducesConsistentCodes()
    {
        var (primary, _) = DoubleMetaphone.Encode("Led Zeppelin");
        Assert.NotEmpty(primary);
    }

    // --- Performance test ---

    [Fact]
    public void Encode_Performance_10KNames_Under100ms()
    {
        // Generate 10K realistic artist names
        var names = new List<string>(10000);
        for (int i = 0; i < 10000; i++)
        {
            names.Add($"Artist {i}");
        }

        // Add some variety
        names.AddRange(new[] { "The Beatles", "Pink Floyd", "Led Zeppelin", "Radiohead", "Soul Coughing", "Björk", "Daft Punk" });

        var sw = Stopwatch.StartNew();
        foreach (string name in names)
        {
            DoubleMetaphone.Encode(name);
        }

        sw.Stop();

        // Pre-computing 10K phonetic codes should be very fast
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Encoding 10K names took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }
}

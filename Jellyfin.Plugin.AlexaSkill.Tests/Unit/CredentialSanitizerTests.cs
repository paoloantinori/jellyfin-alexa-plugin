using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class CredentialSanitizerTests
{
    [Fact]
    public void Sanitize_CleanValue_ReturnsUnchanged()
    {
        Assert.Equal("be308c39-8528-40ac-a1f1-3195ba6a37ca",
            CredentialSanitizer.Sanitize("be308c39-8528-40ac-a1f1-3195ba6a37ca"));
    }

    [Fact]
    public void Sanitize_ZeroWidthSpacesStripped()
    {
        // U+200B zero-width space before and after
        string input = "​be308c39-8528-40ac-a1f1-3195ba6a37ca​";
        Assert.Equal("be308c39-8528-40ac-a1f1-3195ba6a37ca",
            CredentialSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_BomStripped()
    {
        // U+FEFF BOM prepended
        string input = "﻿be308c39-8528-40ac-a1f1-3195ba6a37ca";
        Assert.Equal("be308c39-8528-40ac-a1f1-3195ba6a37ca",
            CredentialSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_DirectionalMarksStripped()
    {
        // U+200E LRM, U+200F RLM, U+202A-LRE
        string input = "‎‏be308c39-8528-40ac-a1f1-3195ba6a37ca‪";
        Assert.Equal("be308c39-8528-40ac-a1f1-3195ba6a37ca",
            CredentialSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_SoftHyphenStripped()
    {
        // U+00AD soft hyphen (common in copy-paste)
        string input = "be308c39­-8528-40ac-a1f1-3195ba6a37ca";
        Assert.Equal("be308c39-8528-40ac-a1f1-3195ba6a37ca",
            CredentialSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_MultipleInvisibleCharsStripped()
    {
        // BOM + zero-width space + word joiner surrounding the value
        string input = "﻿​⁠be308c39-8528-40ac-a1f1-3195ba6a37ca​﻿";
        Assert.Equal("be308c39-8528-40ac-a1f1-3195ba6a37ca",
            CredentialSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_TrimsWhitespace()
    {
        Assert.Equal("secret123", CredentialSanitizer.Sanitize("  secret123  "));
    }

    [Fact]
    public void Sanitize_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CredentialSanitizer.Sanitize(null));
    }

    [Fact]
    public void Sanitize_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CredentialSanitizer.Sanitize(string.Empty));
    }

    [Fact]
    public void Sanitize_WhitespaceOnly_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CredentialSanitizer.Sanitize("   "));
    }

    [Fact]
    public void Sanitize_PreservesPrintableSpecialChars()
    {
        // OAuth secrets can contain +, /, = (base64), hyphens, underscores
        Assert.Equal("abc+def/ghi=jkl-mno_pqr",
            CredentialSanitizer.Sanitize("abc+def/ghi=jkl-mno_pqr"));
    }

    [Fact]
    public void Sanitize_ClientSecretWithInvisibleChars()
    {
        // Realistic case: client secret with BOM + zero-width joiner
        string input = "﻿amzn1.application-oa2-client.abc123secret‍";
        Assert.Equal("amzn1.application-oa2-client.abc123secret",
            CredentialSanitizer.Sanitize(input));
    }
}

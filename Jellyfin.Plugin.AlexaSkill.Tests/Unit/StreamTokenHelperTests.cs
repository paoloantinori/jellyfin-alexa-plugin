using System;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class StreamTokenHelperTests
{
    private const string Secret = "test-secret-please-not-in-prod-32+chars-long!!";
    private const string ItemId = "d2d1167e-aaf8-1877-619a-118b250cc620";

    [Fact]
    public void Mint_Validate_RoundTrip_Succeeds()
    {
        string token = StreamTokenHelper.Mint(ItemId, Secret);
        Assert.True(StreamTokenHelper.TryValidate(token, ItemId, Secret));
    }

    [Fact]
    public void TryValidate_NullToken_ReturnsFalse()
        => Assert.False(StreamTokenHelper.TryValidate(null, ItemId, Secret));

    [Fact]
    public void TryValidate_EmptyToken_ReturnsFalse()
        => Assert.False(StreamTokenHelper.TryValidate(string.Empty, ItemId, Secret));

    [Fact]
    public void TryValidate_MalformedToken_NoDot_ReturnsFalse()
        => Assert.False(StreamTokenHelper.TryValidate("nodothere", ItemId, Secret));

    [Fact]
    public void TryValidate_ExpiredToken_ReturnsFalse()
    {
        // Negative TTL => already expired at mint time.
        string token = StreamTokenHelper.Mint(ItemId, Secret, TimeSpan.FromHours(-1));
        Assert.False(StreamTokenHelper.TryValidate(token, ItemId, Secret));
    }

    [Fact]
    public void TryValidate_WrongItemId_ReturnsFalse()
    {
        string token = StreamTokenHelper.Mint(ItemId, Secret);
        var otherItem = Guid.NewGuid().ToString();
        Assert.False(StreamTokenHelper.TryValidate(token, otherItem, Secret));
    }

    [Fact]
    public void TryValidate_TamperedSignature_ReturnsFalse()
    {
        string token = StreamTokenHelper.Mint(ItemId, Secret);
        // Tamper with the middle of the HMAC portion (not the last char, which is
        // base64url-padding-ambiguous and may decode to the same bytes).
        int dot = token.IndexOf('.');
        int midSig = dot + 1 + 10; // well inside the signature, not near the padding tail
        char c = token[midSig];
        char flipped = c == 'A' ? 'B' : 'A';
        string tampered = token[..midSig] + flipped + token[(midSig + 1)..];
        Assert.False(StreamTokenHelper.TryValidate(tampered, ItemId, Secret));
    }

    [Fact]
    public void TryValidate_WrongSecret_ReturnsFalse()
    {
        string token = StreamTokenHelper.Mint(ItemId, Secret);
        Assert.False(StreamTokenHelper.TryValidate(token, ItemId, "a-completely-different-secret-value"));
    }

    [Fact]
    public void Mint_ProducesUrlSafeString()
    {
        string token = StreamTokenHelper.Mint(ItemId, Secret);
        // Must be safe unescaped in a URL query string: no +, /, =, space.
        Assert.DoesNotContain("+", token);
        Assert.DoesNotContain("/", token);
        Assert.DoesNotContain("=", token);
        Assert.DoesNotContain(" ", token);
        // Format: {expiresUnix}.{hmacBase64Url}
        Assert.Contains(".", token);
    }

    [Fact]
    public void Mint_DefaultTtl_IsLongLived()
    {
        // Default TTL (Config.StreamTokenTtlSeconds, 10h) — a freshly minted token must validate
        // and its expiry must be ~10h in the future (guards against a too-short default).
        string token = StreamTokenHelper.Mint(ItemId, Secret);
        Assert.True(StreamTokenHelper.TryValidate(token, ItemId, Secret));
        long expiresUnix = long.Parse(token.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long ttlSec = expiresUnix - nowUnix;
        Assert.InRange(ttlSec, 9 * 3600, 11 * 3600); // ~10h, +/- 1h slack
    }
}

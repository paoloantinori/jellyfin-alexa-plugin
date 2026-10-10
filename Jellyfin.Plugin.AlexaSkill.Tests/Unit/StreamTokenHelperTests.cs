using System;
using System.Linq;
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

    // ---- JF-767 Finding B: the library-scoped form ----

    private static readonly Guid[] Scope = { Guid.NewGuid(), Guid.NewGuid() };

    /// <summary>
    /// Token surgery for the tamper pins: rebuild a scoped token with its scope field
    /// replaced (the signature is NOT re-minted, so the HMAC still covers the original
    /// scope). A null replacement strips the field, producing the legacy two-field
    /// shape with the scoped token's signature (the widening attack).
    /// </summary>
    private static string WithScopeField(string token, string? scopeField)
    {
        int firstDot = token.IndexOf('.');
        int secondDot = token.IndexOf('.', firstDot + 1);
        return scopeField is null
            ? token[..firstDot] + token[secondDot..]
            : token[..(firstDot + 1)] + scopeField + token[secondDot..];
    }

    [Fact]
    public void MintScoped_UnrestrictedUser_MintsLegacyTwoFieldToken()
    {
        // Null or empty scope = unrestricted: the legacy two-field wire form, byte-identical
        // to Mint, so unrestricted launches keep today's URLs and every legacy consumer.
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, allowedLibraryIds: null);
        Assert.Equal(2, token.Split('.').Length);
        Assert.True(StreamTokenHelper.TryValidate(token, ItemId, Secret, out Guid[]? scope));
        Assert.Null(scope);

        string emptyScope = StreamTokenHelper.MintScoped(ItemId, Secret, Array.Empty<Guid>());
        Assert.Equal(2, emptyScope.Split('.').Length);
    }

    [Fact]
    public void MintScoped_Validate_RoundTrip_ReturnsScope()
    {
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);

        Assert.True(StreamTokenHelper.TryValidate(token, ItemId, Secret, out Guid[]? parsed));
        Assert.NotNull(parsed);
        Assert.Equal(Scope.ToHashSet(), parsed!.ToHashSet());
    }

    [Fact]
    public void MintScoped_Canonicalizes_OrderAndDuplicates()
    {
        // Same membership must mint the same bytes regardless of config order or duplicates:
        // only a real membership change invalidates outstanding tokens. The mint embeds
        // the CURRENT Unix second (StreamTokenHelper expiry stamp), so the two mints
        // must land in the same second for byte equality; a boundary tick between them
        // is a clock artifact, not a canonicalization failure, so the pair re-mints
        // (bounded) when it straddles a tick. A real canonicalization bug fails every
        // attempt and the final assert reddens.
        string? first = null;
        string? second = null;
        for (int attempt = 0; attempt < 20 && first != second; attempt++)
        {
            first = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);
            second = StreamTokenHelper.MintScoped(ItemId, Secret, new[] { Scope[1], Scope[0], Scope[1] });
        }

        Assert.Equal(first, second);
    }

    [Fact]
    public void MintScoped_WrongItemId_ReturnsFalse()
    {
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);
        Assert.False(StreamTokenHelper.TryValidate(token, Guid.NewGuid().ToString(), Secret, out _));
    }

    [Fact]
    public void MintScoped_ExpiredToken_ReturnsFalse()
    {
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope, TimeSpan.FromHours(-1));
        Assert.False(StreamTokenHelper.TryValidate(token, ItemId, Secret, out _));
    }

    [Fact]
    public void TryValidate_ScopeTampered_ReturnsFalse()
    {
        // The HMAC covers the scope: editing the scope field (narrowing OR widening) with
        // the original signature must fail.
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);
        string tampered = WithScopeField(token, Guid.NewGuid().ToString("N"));
        Assert.False(StreamTokenHelper.TryValidate(tampered, ItemId, Secret, out _));
    }

    [Fact]
    public void TryValidate_ScopeFieldDeleted_ReturnsFalse()
    {
        // The widening attack: strip the scope field and present the scoped token as a
        // legacy two-field token. The signature binds the scoped payload, so the legacy
        // payload comparison fails (fail-closed, never a silent unrestricted fallback).
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);
        string stripped = WithScopeField(token, scopeField: null);
        Assert.False(StreamTokenHelper.TryValidate(stripped, ItemId, Secret, out _));
    }

    [Fact]
    public void TryValidate_MalformedScope_ReturnsFalse()
    {
        // A present-but-unparseable scope field fails the whole validation (the mint never
        // produces one; only an attacker can).
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);
        string malformed = WithScopeField(token, "not-a-guid");
        Assert.False(StreamTokenHelper.TryValidate(malformed, ItemId, Secret, out _));
    }

    [Fact]
    public void TryValidate_LegacyToken_ScopeOutIsNull()
    {
        string token = StreamTokenHelper.Mint(ItemId, Secret);
        Assert.True(StreamTokenHelper.TryValidate(token, ItemId, Secret, out Guid[]? scope));
        Assert.Null(scope);
    }

    [Fact]
    public void MintScoped_ProducesUrlSafeString()
    {
        // Must be safe unescaped in a URL query string (it rides the playlist and segment
        // URLs verbatim): no +, /, =, space; the scoped form carries exactly two dots.
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);
        Assert.DoesNotContain("+", token);
        Assert.DoesNotContain("/", token);
        Assert.DoesNotContain("=", token);
        Assert.DoesNotContain(" ", token);
        Assert.Equal(2, token.Split('.').Length - 1);
    }

    /// <summary>
    /// JF-784 leg 2 pin: the scope renders one base64url field of EXACTLY 22
    /// chars per library (the 16 GUID bytes unpadded), the compact form that
    /// bounds the token's per-library cost at 23 chars (field + separator) vs
    /// the superseded 33-char "N"-hex rendering. The token rides every playlist
    /// segment line and every segment request URL, so the per-library width is
    /// the multiplier on both the playlist bytes and the request-line length
    /// (Kestrel's 8192-byte default MaxRequestLineSize). RED on the pre-JF-784
    /// tree: the fields render 32 chars of hex.
    /// </summary>
    [Fact]
    public void MintScoped_RendersCompactBase64UrlPerLibraryScope()
    {
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);
        string scopeField = token.Split('.')[1];
        string[] fields = scopeField.Split(',');

        Assert.Equal(Scope.Length, fields.Length);
        // 22 chars per field is the complete discriminator: a 32-char "N"-hex
        // field (the superseded rendering) cannot fit, so no separate
        // DoesNotContain assert is needed.
        Assert.All(fields, field => Assert.Equal(22, field.Length));
    }

    [Fact]
    public void MintScoped_EmptyScopeField_Rejected()
    {
        // "expires..sig" (empty scope field) is malformed, not unrestricted.
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);
        int firstDot = token.IndexOf('.');
        int secondDot = token.IndexOf('.', firstDot + 1);
        string emptyScope = token[..(firstDot + 1)] + token[secondDot..];
        Assert.False(StreamTokenHelper.TryValidate(emptyScope, ItemId, Secret, out _));
    }

    [Fact]
    public void MintScoped_MalformedScopeNeverValidatesViaThreeArgForm()
    {
        // The discarding (3-arg) form never parses the scope, but a malformed scope
        // field still cannot sneak past it: the signature binds the payload the mint
        // produced, so any edit fails the HMAC first.
        string token = StreamTokenHelper.MintScoped(ItemId, Secret, Scope);
        Assert.True(StreamTokenHelper.TryValidate(WithScopeField(token, RenderedScope()), ItemId, Secret));
        Assert.False(StreamTokenHelper.TryValidate(WithScopeField(token, "not-a-guid"), ItemId, Secret));
        Assert.False(StreamTokenHelper.TryValidate(WithScopeField(token, "not-a-guid"), ItemId, Secret, out _));

        // JF-784: the canonical rendering is the mint's own (compact base64url
        // fields since this task). Derived from a SHUFFLED, DUPLICATED input
        // so the assert is not tautological: if the mint stopped canonicalizing
        // (a dropped OrderBy/Distinct), the shuffled mint's field would differ
        // from the original token's, the substitution would change the signed
        // payload, and the True below would fail (independent coverage the
        // same-input derivation could not give).
        string RenderedScope()
            => StreamTokenHelper.MintScoped(ItemId, Secret, new[] { Scope[1], Scope[0], Scope[1] }).Split('.')[1];
    }
}

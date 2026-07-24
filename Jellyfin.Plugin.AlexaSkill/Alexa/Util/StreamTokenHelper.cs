using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Mints and validates signed, item-scoped, expiring tokens that gate the video-audio streaming
/// endpoints (JF-309). The token binds an item GUID to an expiry via an HMAC-SHA256 signature over
/// the shared server secret, so that a bare item GUID alone can no longer stream an item.
/// </summary>
/// <remarks>
/// Token wire format: <c>{expiresUnix}.{hmacBase64Url}</c> — two dot-separated fields, both
/// URL-safe (base64url without padding for the HMAC, decimal seconds for the expiry). The item id
/// is NOT carried in the token; it comes from the request route and the HMAC binds the two.
/// Signature comparison uses <see cref="CryptographicOperations.FixedTimeEquals"/> (constant-time)
/// to avoid timing-oracle attacks.
/// </remarks>
public static class StreamTokenHelper
{
    private const char Separator = '.';

    /// <summary>
    /// Mint a signed, item-scoped, expiring token.
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID the token is scoped to.</param>
    /// <param name="secret">The shared server secret (HMAC key).</param>
    /// <param name="ttl">Time-to-live; defaults to <see cref="Config.StreamTokenTtlSeconds"/> (10h).</param>
    /// <returns>A URL-safe token string <c>{expiresUnix}.{hmacBase64Url}</c>.</returns>
    public static string Mint(string itemId, string secret, TimeSpan? ttl = null)
    {
        long expiresUnix = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromSeconds(Config.StreamTokenTtlSeconds)).ToUnixTimeSeconds();
        return MintAt(itemId, secret, expiresUnix);
    }

    /// <summary>
    /// Mint a token with an explicit expiry (Unix seconds). Factored out so tests can mint
    /// already-expired tokens deterministically without touching the clock.
    /// </summary>
    internal static string MintAt(string itemId, string secret, long expiresUnix)
    {
        string payload = Payload(itemId, expiresUnix);
        byte[] hmac = HMACSHA256.HashData(Encoding.ASCII.GetBytes(secret), Encoding.ASCII.GetBytes(payload));
        return expiresUnix.ToString(CultureInfo.InvariantCulture) + Separator + Base64Url(hmac);
    }

    /// <summary>
    /// Validate a token against an item id and the shared secret. Returns false for any missing,
    /// malformed, expired, tampered, or wrong-item token. Constant-time signature comparison.
    /// </summary>
    public static bool TryValidate(string? token, string itemId, string secret)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        int dot = token.IndexOf(Separator);
        if (dot <= 0 || dot == token.Length - 1)
        {
            return false;
        }

        string expiresStr = token[..dot];
        if (!long.TryParse(expiresStr, NumberStyles.None, CultureInfo.InvariantCulture, out long expiresUnix))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresUnix)
        {
            return false;
        }

        string presentedSig = token[(dot + 1)..];
        byte[]? presentedBytes = TryDecodeBase64Url(presentedSig);
        if (presentedBytes is null)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(Encoding.ASCII.GetBytes(secret), Encoding.ASCII.GetBytes(Payload(itemId, expiresUnix)));
        return presentedBytes.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presentedBytes, expected);
    }

    private static string Payload(string itemId, long expiresUnix)
        => itemId + "|" + expiresUnix.ToString(CultureInfo.InvariantCulture);

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? TryDecodeBase64Url(string s)
    {
        // base64url → standard base64: replace URL-safe chars, pad to a multiple of 4.
        string base64 = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
            case 1: return null; // malformed — length 1 mod 4 is never valid base64
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

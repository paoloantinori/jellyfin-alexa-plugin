using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Mints and validates signed, item-scoped, expiring tokens that gate the video-audio streaming
/// endpoints (JF-309). The token binds an item GUID to an expiry via an HMAC-SHA256 signature over
/// the shared server secret, so that a bare item GUID alone can no longer stream an item.
/// </summary>
/// <remarks>
/// Token wire format, two shapes distinguished by the field count (neither base64url nor the
/// scope alphabet contains the separator, so the count is deterministic):
/// <list type="bullet">
/// <item>legacy / unrestricted: <c>{expiresUnix}.{hmacBase64Url}</c>, two dot-separated fields,
/// both URL-safe (base64url without padding for the HMAC, decimal seconds for the expiry);</item>
/// <item>library-scoped (JF-767): <c>{expiresUnix}.{scope}.{hmacBase64Url}</c>, where the scope
/// is the launching user's allowed library GUIDs in "N" form, comma-joined in canonical
/// (sorted, deduplicated) order. The HMAC covers the scope, so an edited or stripped scope
/// field invalidates the signature (fail-closed, including the widening attack of deleting
/// the field to fall back to the unrestricted shape).</item>
/// </list>
/// The item id is NOT carried in the token; it comes from the request route and the HMAC binds the two.
/// Signature comparison uses <see cref="CryptographicOperations.FixedTimeEquals"/> (constant-time)
/// to avoid timing-oracle attacks.
/// </remarks>
public static class StreamTokenHelper
{
    private const char Separator = '.';
    private const char ScopeSeparator = ',';

    /// <summary>
    /// Mint a signed, item-scoped, expiring token.
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID the token is scoped to.</param>
    /// <param name="secret">The shared server secret (HMAC key).</param>
    /// <param name="ttl">Time-to-live; defaults to <see cref="Config.StreamTokenTtlSeconds"/> (10h).</param>
    /// <returns>A URL-safe token string <c>{expiresUnix}.{hmacBase64Url}</c>.</returns>
    public static string Mint(string itemId, string secret, TimeSpan? ttl = null)
    {
        long expiresUnix = ExpiresUnix(ttl);
        return MintCore(itemId, secret, expiresUnix, scopePart: null);
    }

    /// <summary>
    /// Mint a token with an explicit expiry (Unix seconds). Factored out so tests can mint
    /// already-expired tokens deterministically without touching the clock.
    /// </summary>
    internal static string MintAt(string itemId, string secret, long expiresUnix)
        => MintCore(itemId, secret, expiresUnix, scopePart: null);

    /// <summary>
    /// Mint a library-scoped token (JF-767 Finding B): the concat endpoint has no session
    /// user on its token-gated HTTP path, so the launching user's library restriction rides
    /// IN the token and the endpoint applies it when enumerating. A null or empty scope
    /// (unrestricted user) mints the legacy two-field form byte-identically, so unrestricted
    /// launches keep today's URLs and tokens.
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID the token is scoped to.</param>
    /// <param name="secret">The shared server secret (HMAC key).</param>
    /// <param name="allowedLibraryIds">The launching user's allowed library GUIDs
    /// (<see cref="LibraryFilter.GetAllowedLibraryIds"/> output), or null/empty when
    /// unrestricted.</param>
    /// <param name="ttl">Time-to-live; defaults to <see cref="Config.StreamTokenTtlSeconds"/> (10h).</param>
    /// <returns>A URL-safe token string, the scoped three-field or legacy two-field form.</returns>
    public static string MintScoped(string itemId, string secret, Guid[]? allowedLibraryIds, TimeSpan? ttl = null)
        => MintCore(itemId, secret, ExpiresUnix(ttl), RenderScope(allowedLibraryIds));

    /// <summary>
    /// The ONE mint core both public forms share: null scopePart renders the legacy
    /// two-field wire form, a canonical scope string the three-field scoped form.
    /// </summary>
    private static string MintCore(string itemId, string secret, long expiresUnix, string? scopePart)
    {
        string payload = Payload(itemId, expiresUnix, scopePart);
        byte[] hmac = HMACSHA256.HashData(Encoding.ASCII.GetBytes(secret), Encoding.ASCII.GetBytes(payload));
        string expires = expiresUnix.ToString(CultureInfo.InvariantCulture);
        return scopePart is null
            ? expires + Separator + Base64Url(hmac)
            : expires + Separator + scopePart + Separator + Base64Url(hmac);
    }

    private static long ExpiresUnix(TimeSpan? ttl)
        => DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromSeconds(Config.StreamTokenTtlSeconds)).ToUnixTimeSeconds();

    /// <summary>
    /// Validate a token against an item id and the shared secret. Returns false for any missing,
    /// malformed, expired, tampered, or wrong-item token. Constant-time signature comparison.
    /// Never parses the scope field (the discarding hot paths: per-segment validation).
    /// </summary>
    public static bool TryValidate(string? token, string itemId, string secret)
        => TryValidateCore(token, itemId, secret, out _);

    /// <summary>
    /// The scope-reading form of <see cref="TryValidate(string?, string, string)"/> (JF-767):
    /// on success, <paramref name="allowedLibraryIds"/> carries the token's library scope
    /// (the minted <see cref="LibraryFilter.GetAllowedLibraryIds"/> output), or null for the
    /// legacy / unrestricted two-field shape. A present-but-malformed scope field fails the
    /// whole validation (fail-closed, never a silent fallback to unrestricted); the parse
    /// runs only after the signature already proved the token genuine.
    /// </summary>
    /// <param name="token">The presented token.</param>
    /// <param name="itemId">The item GUID from the request route.</param>
    /// <param name="secret">The shared server secret (HMAC key).</param>
    /// <param name="allowedLibraryIds">On success: the scope GUIDs, or null when the token
    /// carries none.</param>
    public static bool TryValidate(string? token, string itemId, string secret, out Guid[]? allowedLibraryIds)
    {
        if (!TryValidateCore(token, itemId, secret, out string? scopePart))
        {
            allowedLibraryIds = null;
            return false;
        }

        allowedLibraryIds = scopePart is null ? null : ParseScope(scopePart);
        if (scopePart is not null && allowedLibraryIds is null)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The ONE validation core both public forms share: shape, expiry, and signature over
    /// the full payload (scope included). Outs the RAW scope field (null for the legacy
    /// shape) so the discarding overload never pays the GUID parse, and the scope-reading
    /// overload parses only on the success path.
    /// </summary>
    private static bool TryValidateCore(string? token, string itemId, string secret, out string? scopePart)
    {
        scopePart = null;
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

        string rest = token[(dot + 1)..];
        int scopeDot = rest.IndexOf(Separator);
        string presentedSig;
        if (scopeDot < 0)
        {
            // Legacy two-field shape: no scope, the rest is the signature.
            presentedSig = rest;
        }
        else
        {
            if (scopeDot == 0 || scopeDot == rest.Length - 1)
            {
                return false;
            }

            scopePart = rest[..scopeDot];
            presentedSig = rest[(scopeDot + 1)..];
        }

        byte[]? presentedBytes = TryDecodeBase64Url(presentedSig);
        if (presentedBytes is null)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(Encoding.ASCII.GetBytes(secret), Encoding.ASCII.GetBytes(Payload(itemId, expiresUnix, scopePart)));
        return presentedBytes.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presentedBytes, expected);
    }

    /// <summary>
    /// The canonical scope rendering: "N"-form GUIDs, deduplicated and sorted, comma-joined;
    /// null when the scope is empty or absent (the unrestricted user, whose mint is the
    /// legacy two-field form). Canonical so the same scope set always mints the same token
    /// bytes regardless of config order (only a real membership change invalidates
    /// outstanding tokens). "N" hex and the comma are URL-safe and contain no token
    /// separator. BOUND (filed as JF-784): the rendering is one ~33-char field per
    /// library with no cap; the token rides every playlist segment line and request
    /// URL, so a pathological library count (hundreds) bloats playlists toward
    /// player/parser and request-line limits. Household scale (single-digit libraries)
    /// is far inside every limit; a compact or digest encoding is the tracked fix shape.
    /// </summary>
    private static string? RenderScope(Guid[]? allowedLibraryIds)
        => allowedLibraryIds is not { Length: > 0 }
            ? null
            : string.Join(ScopeSeparator, allowedLibraryIds
                .Select(g => g.ToString("N", CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>
    /// Parse a scope field back to its GUIDs; null when any element fails to parse (the
    /// mint never produces such a field, so the caller treats it as invalid).
    /// </summary>
    private static Guid[]? ParseScope(string scopePart)
    {
        string[] parts = scopePart.Split(ScopeSeparator);
        var ids = new Guid[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!Guid.TryParse(parts[i], out ids[i]))
            {
                return null;
            }
        }

        return ids;
    }

    private static string Payload(string itemId, long expiresUnix, string? scopePart)
        => scopePart is null
            ? itemId + "|" + expiresUnix.ToString(CultureInfo.InvariantCulture)
            : itemId + "|" + expiresUnix.ToString(CultureInfo.InvariantCulture) + "|" + scopePart;

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

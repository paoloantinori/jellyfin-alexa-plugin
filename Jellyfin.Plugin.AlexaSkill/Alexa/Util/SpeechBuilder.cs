using System;
using Alexa.NET;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Pure speech/SSML output construction (JF-315 batch 1): response speech builders,
/// SSML/escaping helpers, and the localized Ask fallback (JF-407). Static by design:
/// every member is a pure function of its inputs (locale files + arguments), no
/// handler instance state, so it is directly unit-testable without injection.
/// Members moved verbatim from BaseHandler; call sites migrated mechanically.
/// </summary>
public static class SpeechBuilder
{
    /// <summary>
    /// Build a Tell response using SSML for more natural speech.
    /// </summary>
    /// <param name="ssml">SSML content (without the outer speak tags).</param>
    /// <returns>A SkillResponse with SSML output speech.</returns>
    public static SkillResponse TellSsml(string ssml)
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                OutputSpeech = new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" }
            }
        };
    }

    /// <summary>
    /// Build an Ask response using SSML for more natural speech, with an SSML reprompt.
    /// </summary>
    /// <param name="ssml">SSML content for the main speech (without speak tags).</param>
    /// <param name="repromptSsml">SSML content for the reprompt (without speak tags).</param>
    /// <returns>A SkillResponse with SSML output speech and reprompt.</returns>
    public static SkillResponse AskSsml(string ssml, string repromptSsml)
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = false,
                OutputSpeech = new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" },
                Reprompt = new Reprompt { OutputSpeech = new SsmlOutputSpeech { Ssml = $"<speak>{repromptSsml}</speak>" } }
            }
        };
    }

    /// <summary>
    /// Build an Ask response using SSML for speech and plain text for reprompt.
    /// </summary>
    /// <param name="ssml">SSML content for the main speech (without speak tags).</param>
    /// <param name="reprompt">Plain text reprompt.</param>
    /// <returns>A SkillResponse with SSML output speech and plain text reprompt.</returns>
    public static SkillResponse AskSsml(string ssml, Reprompt reprompt)
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = false,
                OutputSpeech = new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" },
                Reprompt = reprompt
            }
        };
    }

    /// <summary>
    /// Try to get an SSML-enhanced string from locale files.
    /// Returns null if no SSML key exists, allowing fallback to plain text.
    /// </summary>
    /// <param name="key">The SSML key (e.g. "NowPlayingSsml").</param>
    /// <param name="locale">The locale identifier.</param>
    /// <param name="args">Optional format arguments. String values are interpolated into
    /// SSML as-is, so callers MUST pre-escape reserved XML chars with EscapeXml (unlike
    /// BuildOutputSpeech, which escapes internally).</param>
    /// <returns>The formatted SSML string, or null if the key doesn't exist.</returns>
    public static string? GetSsml(string key, string locale, params object[] args)
    {
        string template = ResponseStrings.Get(key, locale);
        if (template == key)
        {
            return null;
        }

        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);
    }

    /// <summary>
    /// Build an OutputSpeech using SSML with plaintext fallback. Tries the SSML key
    /// first; falls back to the plain key if SSML is unavailable. Callers pass RAW
    /// (unescaped) args: the SSML path escapes reserved XML chars here, while the
    /// plain-text fallback keeps them raw, so an ampersand in a title is spoken as
    /// a real ampersand rather than the escaped SSML entity.
    /// </summary>
    public static IOutputSpeech BuildOutputSpeech(string ssmlKey, string plainKey, string locale, params object[] args)
    {
        string? ssml = GetSsml(ssmlKey, locale, EscapeStringArgs(args));
        if (ssml != null)
        {
            return new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" };
        }

        return new PlainTextOutputSpeech { Text = ResponseStrings.Get(plainKey, locale, args) };
    }

    /// <summary>
    /// Build a session-opening Ask using SSML when available, with a plaintext fallback
    /// (JF-407 item 3). Consolidates the hand-written GetSsml-then-AskSsml-or-Ask
    /// pattern that was duplicated across DisambiguationHelper (3x),
    /// FallbackIntentHandler, LaunchRequestHandler, and BaseHandler (2x), where the
    /// reprompt-key handling and XML escaping drifted between sites. Args are RAW
    /// (unescaped): the SSML path escapes them internally, the plaintext path keeps
    /// them raw. The reprompt is always emitted as PlainText. The one behavior change
    /// from the sites it replaced (review 2026-08-29): the LaunchRequestHandler resume
    /// site previously used the AskSsml(string, string) overload, which wrapped the
    /// reprompt in speak tags (SSML output); this helper emits PlainText, which is
    /// strictly more robust (the old wrapping would produce INVALID SSML if a future
    /// localized reprompt contained a raw XML char) but is a wire-format difference.
    /// The Welcome flow's dual SSML prompt+reprompt stays hand-written in
    /// LaunchRequestHandler (it is the only site with an SSML reprompt variant).
    /// </summary>
    /// <param name="ssmlKey">The ResponseStrings key for the SSML prompt variant.</param>
    /// <param name="textKey">The ResponseStrings key for the plain-text prompt variant.</param>
    /// <param name="repromptKey">The ResponseStrings key for the plain-text reprompt.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="args">Format args for both prompt variants (raw, not XML-escaped).</param>
    /// <returns>A session-opening Ask response.</returns>
    public static SkillResponse AskLocalized(
        string ssmlKey, string textKey, string repromptKey, string locale, params object[] args)
    {
        string reprompt = ResponseStrings.Get(repromptKey, locale);
        string? ssml = GetSsml(ssmlKey, locale, EscapeStringArgs(args));
        if (ssml != null)
        {
            return AskSsml(ssml, new Reprompt(reprompt));
        }

        string prompt = ResponseStrings.Get(textKey, locale, args);
        return ResponseBuilder.Ask(prompt, new Reprompt(reprompt));
    }

    /// <summary>
    /// Escape SSML-reserved chars in string args for safe interpolation into &lt;speak&gt;.
    /// Non-string args (counts, etc.) pass through unchanged.
    /// </summary>
    private static object[] EscapeStringArgs(object[] args)
    {
        if (args.Length == 0)
        {
            return args;
        }

        var escaped = new object[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            escaped[i] = args[i] is string s ? EscapeXml(s) : args[i];
        }

        return escaped;
    }

    /// <summary>
    /// The now-playing announce shared by every video-launch handler. Wraps
    /// BuildOutputSpeech with the NowPlaying SSML/plain keys; the title is escaped for SSML.
    /// The announce gate is a REQUIRED parameter (JF-315 batch 5, closing the batch-1
    /// follow-up): it is a per-user policy resolved by the caller
    /// (PlaybackLaunchBuilder.GetAnnounceNowPlaying / GetAnnounceAudioPlays), not a
    /// util default, and no call site relies on the former implicit true.
    /// </summary>
    public static IOutputSpeech? BuildNowPlayingSpeech(string name, string locale, bool announceOn)
        => announceOn ? BuildOutputSpeech("NowPlayingSsml", "NowPlaying", locale, name) : null;
    /// <summary>
    /// Escapes special XML characters in text for safe inclusion in SSML.
    /// </summary>
    /// <param name="text">The text to escape.</param>
    /// <returns>The XML-escaped text.</returns>
    internal static string EscapeXml(string? text)
    {
        return (text ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}
}

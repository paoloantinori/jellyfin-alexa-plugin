using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-501 test seam: the video-launch announce moved from the final response's
/// OutputSpeech to the progressive-response vehicle, so tests subclass the concrete
/// handlers and override <c>SendProgressiveResponse</c> to record what would be
/// spoken instead of hitting the Alexa API. Records every message in send order
/// (the "SearchingMedia" ping AND the announce), so announce assertions must target
/// announce-specific fragments (the title, "next episode", ...) rather than
/// "message count == 1".
/// </summary>
internal sealed class ProgressiveSpeechCapture
{
    private readonly List<string> _messages = new();

    /// <summary>Gets every recorded message joined with newlines (multi-message assertions).</summary>
    public string AllText => string.Join("\n", _messages);

    /// <summary>
    /// The override target for the recording handler subclasses: records and reports a
    /// successful send (mirrors the production Task&lt;bool&gt; contract; a test that needs
    /// a FAILED send overrides to return false itself).
    /// </summary>
    /// <param name="context">The Alexa context (unused).</param>
    /// <param name="request">The skill request (unused).</param>
    /// <param name="message">The progressive message.</param>
    /// <returns>True (the production send never faults; it reports success or failure).</returns>
    public Task<bool> Record(Context context, Request request, string message)
    {
        _messages.Add(message);
        return Task.FromResult(true);
    }

    /// <summary>
    /// True when any recorded progressive message contains the fragment (the announce
    /// when the fragment is announce-specific; the SearchingMedia ping never matches
    /// announce fragments like the item title).
    /// </summary>
    /// <param name="fragment">Case-sensitive fragment to look for.</param>
    /// <returns>True when at least one message contains it.</returns>
    public bool Contains(string fragment)
        => _messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));
}

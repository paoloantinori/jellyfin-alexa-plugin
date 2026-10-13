using System.Linq;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE request-synthesis core the delegation sites share (the JF-624 item 4
/// tap contract, extracted 2026-10-13 when the JF-876 song-carrier delegation
/// became its second consumer): a request handed to another handler must look
/// EXACTLY like what the spoken ask would have delivered, so the delegated
/// handler's own slot reads, locale resolution, and progressive sends behave
/// identically. Each slot's Name equals its key; the RequestId and Locale ride
/// the source request (without RequestId every progressive send the delegated
/// flow makes addresses a null request and always fails; without the Locale the
/// delegated response falls back to the default locale's speech). Lives in Util
/// per the JF-715 home rule: the one definition sits in a neutral home, not on
/// the handler the first consumer happened to be.
/// </summary>
public static class IntentRequestSynthesis
{
    /// <summary>
    /// Builds the synthesized intent request: intent name, source RequestId and
    /// Locale, and slots as (name, value) pairs keyed and named alike.
    /// </summary>
    /// <param name="intentName">The target intent name.</param>
    /// <param name="requestId">The source request's id.</param>
    /// <param name="locale">The source request's locale.</param>
    /// <param name="slots">The (slot name, value) pairs to fill.</param>
    /// <returns>The synthesized intent request.</returns>
    public static IntentRequest Synthesize(
        string intentName,
        string? requestId,
        string? locale,
        params (string Slot, string Value)[] slots)
    {
        return new IntentRequest
        {
            RequestId = requestId,
            Intent = new Intent
            {
                Name = intentName,
                Slots = slots.ToDictionary(
                    s => s.Slot,
                    s => new Slot { Name = s.Slot, Value = s.Value })
            },
            Locale = locale
        };
    }
}

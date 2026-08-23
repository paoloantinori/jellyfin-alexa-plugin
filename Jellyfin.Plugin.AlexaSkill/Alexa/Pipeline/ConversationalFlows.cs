#nullable enable
using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Response;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Conversational-flow mutual exclusion (JF-398). The interceptor merges incoming session
/// attributes onto every open-session response, so keys of DIFFERENT flows used to coexist
/// (a resume offer issued during an active pagination carried both), forcing the Yes/No
/// handlers to resolve collisions with a hard-coded priority chain and letting stale state
/// act later (the JF-394 class). Writers now declare which flow they are activating via
/// <see cref="MarkOthersInactive"/>: every OTHER flow's keys are marked for removal, so at
/// most one conversational flow is live per session. Backward compatible: the key formats
/// are unchanged, so in-flight sessions across a deploy keep working.
/// </summary>
public static class ConversationalFlows
{
    /// <summary>Session keys owned by the FindSong conversational search flow.</summary>
    public static readonly string[] FindSongKeys = { Handler.FindSongIntentHandler.SessionDataKey };

    /// <summary>Session keys owned by the resume-offer flow.</summary>
    public static readonly string[] ResumeKeys = { Handler.ResumeHelper.ResumeStateKey };

    /// <summary>Session keys owned by the list-pagination flow.</summary>
    public static readonly string[] PaginationKeys = { Handler.ListPaginationHelper.AttrKey };

    /// <summary>
    /// Session keys owned by the disambiguation flow, including the cross-media artist
    /// suggestion flavor (which reuses the disambig_* keys and adds the not-found context).
    /// </summary>
    public static readonly string[] DisambiguationKeys =
    {
        Handler.DisambiguationHelper.AttrMatches, Handler.DisambiguationHelper.AttrIndex,
        Handler.DisambiguationHelper.AttrType,
        "crossmedia_notfound_query", "crossmedia_notfound_type"
    };

    private static readonly string[] AllFlowKeys = FindSongKeys
        .Concat(ResumeKeys)
        .Concat(PaginationKeys)
        .Concat(DisambiguationKeys)
        .ToArray();

    /// <summary>
    /// Marks every OTHER flow's session keys for removal on the response, so the flow being
    /// activated becomes the only live conversational state after the interceptor merge.
    /// </summary>
    /// <param name="response">The response that activates a flow.</param>
    /// <param name="activeKeys">The session keys owned by the flow being activated.</param>
    public static void MarkOthersInactive(SkillResponse response, params string[] activeKeys)
    {
        IEnumerable<string> others = AllFlowKeys.Except(activeKeys);

        SessionAttributeRemoval.Mark(response, others.ToArray());
    }
}

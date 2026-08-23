#nullable enable
using System.Collections.Generic;
using Alexa.NET.Response;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Marks session attribute keys for REMOVAL in a response (JF-394).
/// <see cref="SessionAttributesInterceptor"/> merges incoming session attributes onto
/// every open-session response; a flow that has CONCLUDED (e.g. the user declined the
/// resume offer) needs to drop its key instead of letting it ride along. The handler
/// sets the <see cref="MarkerKey"/> attribute to the list of keys to strip; the
/// interceptor removes them from the merged output and never sends the marker to Alexa.
/// </summary>
public static class SessionAttributeRemoval
{
    /// <summary>
    /// The session-attribute key carrying the removal list. Never reaches Alexa:
    /// the interceptor strips it after applying the removals.
    /// </summary>
    public const string MarkerKey = "__remove_attributes";

    /// <summary>
    /// Marks the given attribute keys for removal on the response.
    /// </summary>
    /// <param name="response">The skill response being built.</param>
    /// <param name="keys">The session-attribute keys to drop from the merged output.</param>
    public static void Mark(SkillResponse response, params string[] keys)
    {
        response.SessionAttributes ??= new Dictionary<string, object>();
        Mark(response.SessionAttributes, keys);
    }

    /// <summary>
    /// Marks the given attribute keys for removal on an attributes dictionary that is
    /// (or will become) a response's SessionAttributes. Used by flow writers that build
    /// the dictionary directly (e.g. ListPaginationHelper.WriteState).
    /// </summary>
    /// <param name="attributes">The response session-attributes dictionary.</param>
    /// <param name="keys">The session-attribute keys to drop from the merged output.</param>
    public static void Mark(Dictionary<string, object> attributes, params string[] keys)
    {
        var list = new List<object>(keys.Length);
        foreach (string key in keys)
        {
            list.Add(key);
        }

        if (attributes.TryGetValue(MarkerKey, out object? existing)
            && existing is IEnumerable<object> existingList)
        {
            list.InsertRange(0, existingList);
        }

        attributes[MarkerKey] = list;
    }
}

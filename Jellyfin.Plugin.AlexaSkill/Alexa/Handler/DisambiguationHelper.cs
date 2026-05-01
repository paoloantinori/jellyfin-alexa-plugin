using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Alexa.NET;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Helper for search disambiguation with Yes/No dialogue.
/// Stores match state in Alexa session attributes.
/// </summary>
internal static class DisambiguationHelper
{
    private const string AttrMatches = "disambig_matches";
    private const string AttrIndex = "disambig_index";
    private const string AttrType = "disambig_type";

    public const string MediaTypeSong = "song";
    public const string MediaTypeAlbum = "album";
    public const string MediaTypeArtist = "artist";
    public const string MediaTypeVideo = "video";
    public const string MediaTypePlaylist = "playlist";

    /// <summary>
    /// Check if session attributes contain active disambiguation state.
    /// </summary>
    public static bool HasDisambiguationState(Dictionary<string, object>? sessionAttributes)
    {
        return sessionAttributes != null
            && sessionAttributes.ContainsKey(AttrMatches)
            && sessionAttributes.ContainsKey(AttrType);
    }

    /// <summary>
    /// Build a disambiguation Ask response for the first match.
    /// </summary>
    public static SkillResponse AskFirstMatch(
        List<(Guid Id, string Name)> matches,
        string mediaType,
        string locale)
    {
        var matchList = matches.Take(3).Select(m => new MatchInfo { Id = m.Id.ToString(), Name = m.Name }).ToList();
        int index = 0;

        string prompt = ResponseStrings.Get("DisambiguatePrompt", locale, matchList[index].Name);
        string reprompt = ResponseStrings.Get("DisambiguateReprompt", locale);

        var response = ResponseBuilder.Ask(prompt, new Reprompt(reprompt));
        response.SessionAttributes = BuildAttributes(matchList, index, mediaType);
        return response;
    }

    /// <summary>
    /// Build a disambiguation Ask response for the next match (after No).
    /// </summary>
    public static SkillResponse AskNextMatch(
        List<MatchInfo> matches,
        int nextIndex,
        string mediaType,
        string locale)
    {
        string prompt = ResponseStrings.Get("DisambiguateNext", locale, matches[nextIndex].Name);
        string reprompt = ResponseStrings.Get("DisambiguateReprompt", locale);

        var response = ResponseBuilder.Ask(prompt, new Reprompt(reprompt));
        response.SessionAttributes = BuildAttributes(matches, nextIndex, mediaType);
        return response;
    }

    /// <summary>
    /// Build a "no more matches" Tell response.
    /// </summary>
    public static SkillResponse NoMoreMatches(string locale)
    {
        return ResponseBuilder.Tell(ResponseStrings.Get("NoMoreMatches", locale));
    }

    /// <summary>
    /// Read disambiguation state from session attributes.
    /// </summary>
    public static (List<MatchInfo> Matches, int Index, string MediaType)? ReadState(Dictionary<string, object>? sessionAttributes)
    {
        if (!HasDisambiguationState(sessionAttributes))
        {
            return null;
        }

        string matchesJson = sessionAttributes![AttrMatches]?.ToString() ?? "[]";
        var matches = JsonConvert.DeserializeObject<List<MatchInfo>>(matchesJson) ?? new List<MatchInfo>();
        int index = Convert.ToInt32(sessionAttributes[AttrIndex], CultureInfo.InvariantCulture);
        string mediaType = sessionAttributes[AttrType]?.ToString() ?? string.Empty;

        return (matches, index, mediaType);
    }

    private static Dictionary<string, object> BuildAttributes(List<MatchInfo> matches, int index, string mediaType)
    {
        return new Dictionary<string, object>
        {
            [AttrMatches] = JsonConvert.SerializeObject(matches),
            [AttrIndex] = index,
            [AttrType] = mediaType
        };
    }

    /// <summary>
    /// Serializable match info stored in session attributes.
    /// </summary>
    public class MatchInfo
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
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
    public const string MediaTypePodcast = "podcast";

    /// <summary>
    /// Check if session attributes contain active disambiguation state.
    /// </summary>
    /// <param name="sessionAttributes">The session attributes dictionary.</param>
    /// <returns>True if active disambiguation state is present.</returns>
    public static bool HasDisambiguationState(Dictionary<string, object>? sessionAttributes)
    {
        return sessionAttributes != null
            && sessionAttributes.ContainsKey(AttrMatches)
            && sessionAttributes.ContainsKey(AttrType);
    }

    /// <summary>
    /// Build a disambiguation Ask response for the first match.
    /// </summary>
    /// <param name="matches">The list of candidate matches.</param>
    /// <param name="mediaType">The media type being disambiguated.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <returns>A disambiguation Ask response.</returns>
    public static SkillResponse AskFirstMatch(
        List<(Guid Id, string Name)> matches,
        string mediaType,
        string locale)
    {
        var matchList = matches.Take(3).Select(m => new MatchInfo { Id = m.Id.ToString(), Name = m.Name }).ToList();
        int index = 0;

        string? promptSsml = BaseHandler.GetSsml("DisambiguatePromptSsml", locale, BaseHandler.EscapeXml(matchList[index].Name));
        string reprompt = ResponseStrings.Get("DisambiguateReprompt", locale);

        SkillResponse response;
        if (promptSsml != null)
        {
            response = BaseHandler.AskSsml(promptSsml, new Reprompt(reprompt));
        }
        else
        {
            string prompt = ResponseStrings.Get("DisambiguatePrompt", locale, matchList[index].Name);
            response = ResponseBuilder.Ask(prompt, new Reprompt(reprompt));
        }

        response.SessionAttributes = BuildAttributes(matchList, index, mediaType);
        return response;
    }

    /// <summary>
    /// Build a disambiguation Ask response for the first match, with optional APL carousel.
    /// </summary>
    /// <param name="matches">The list of candidate matches with optional art URLs.</param>
    /// <param name="mediaType">The media type being disambiguated.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <param name="context">The Alexa request context for APL capability detection, or null.</param>
    /// <returns>A disambiguation Ask response, with carousel directive if APL is supported.</returns>
    public static SkillResponse AskFirstMatch(
        List<(Guid Id, string Name, string? ArtUrl)> matches,
        string mediaType,
        string locale,
        Context? context = null)
    {
        var matchList = matches.Take(3).Select(m => new MatchInfo { Id = m.Id.ToString(), Name = m.Name, ArtUrl = m.ArtUrl }).ToList();
        int index = 0;

        string? promptSsml = BaseHandler.GetSsml("DisambiguatePromptSsml", locale, BaseHandler.EscapeXml(matchList[index].Name));
        string reprompt = ResponseStrings.Get("DisambiguateReprompt", locale);

        SkillResponse response;
        if (promptSsml != null)
        {
            response = BaseHandler.AskSsml(promptSsml, new Reprompt(reprompt));
        }
        else
        {
            string prompt = ResponseStrings.Get("DisambiguatePrompt", locale, matchList[index].Name);
            response = ResponseBuilder.Ask(prompt, new Reprompt(reprompt));
        }

        response.SessionAttributes = BuildAttributes(matchList, index, mediaType);

        if (context != null && AplHelper.DeviceSupportsApl(context) && AplHelper.VisualsEnabled)
        {
            var carouselItems = matchList
                .Select(m => new ListDisplayItem(m.Name, m.Id, null, m.ArtUrl))
                .ToList();

            var directive = AplHelper.BuildCarouselDirective(
                ResponseStrings.Get("DisambiguateCarouselTitle", locale),
                carouselItems,
                "disambiguation",
                context);

            if (directive != null)
            {
                response.Response.Directives.Add(directive);
            }
        }

        return response;
    }

    /// <summary>
    /// Build a disambiguation Ask response for the next match (after No).
    /// </summary>
    /// <param name="matches">The list of candidate matches.</param>
    /// <param name="nextIndex">The index of the next match to present.</param>
    /// <param name="mediaType">The media type being disambiguated.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <returns>A disambiguation Ask response.</returns>
    public static SkillResponse AskNextMatch(
        List<MatchInfo> matches,
        int nextIndex,
        string mediaType,
        string locale)
    {
        string? promptSsml = BaseHandler.GetSsml("DisambiguateNextSsml", locale, BaseHandler.EscapeXml(matches[nextIndex].Name));
        string reprompt = ResponseStrings.Get("DisambiguateReprompt", locale);

        SkillResponse response;
        if (promptSsml != null)
        {
            response = BaseHandler.AskSsml(promptSsml, new Reprompt(reprompt));
        }
        else
        {
            string prompt = ResponseStrings.Get("DisambiguateNext", locale, matches[nextIndex].Name);
            response = ResponseBuilder.Ask(prompt, new Reprompt(reprompt));
        }

        response.SessionAttributes = BuildAttributes(matches, nextIndex, mediaType);
        return response;
    }

    /// <summary>
    /// Build a "no more matches" Tell response.
    /// </summary>
    /// <param name="locale">The locale for localized responses.</param>
    /// <returns>A Tell response indicating no more matches.</returns>
    public static SkillResponse NoMoreMatches(string locale)
    {
        return ResponseBuilder.Tell(ResponseStrings.Get("NoMoreMatches", locale));
    }

    /// <summary>
    /// Read disambiguation state from session attributes.
    /// </summary>
    /// <param name="sessionAttributes">The session attributes dictionary.</param>
    /// <returns>The disambiguation state tuple, or null if not present.</returns>
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

        [JsonProperty("artUrl")]
        public string? ArtUrl { get; set; }
    }
}

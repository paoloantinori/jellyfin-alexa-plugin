using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using MediaBrowser.Controller.Library;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Manages list pagination state in session attributes.
/// Stores item IDs from list queries so ShowMoreIntent can page through
/// them without re-querying the library.
/// </summary>
internal static class ListPaginationHelper
{
    private const string AttrKey = "pagination_state";

    /// <summary>
    /// Identifies which list type produced the paginated results.
    /// </summary>
    public enum ListType
    {
        BrowseLibrary,
        ArtistLibrary,
        InProgress,
        Queue,
        RecentlyAdded
    }

    /// <summary>
    /// Serializable pagination state stored in session attributes.
    /// </summary>
    internal class PaginationState
    {
        [JsonProperty("type")]
        public ListType Type { get; set; }

        [JsonProperty("itemIds")]
        public string[] ItemIds { get; set; } = Array.Empty<string>();

        [JsonProperty("offset")]
        public int CurrentOffset { get; set; }

        [JsonProperty("pageSize")]
        public int PageSize { get; set; }
    }

    /// <summary>
    /// Check if session attributes contain active pagination state.
    /// </summary>
    public static bool HasPaginationState(Dictionary<string, object>? sessionAttributes)
    {
        return sessionAttributes != null
            && sessionAttributes.ContainsKey(AttrKey);
    }

    /// <summary>
    /// Read pagination state from session attributes.
    /// </summary>
    public static PaginationState? ReadState(Dictionary<string, object>? sessionAttributes)
    {
        if (!HasPaginationState(sessionAttributes))
        {
            return null;
        }

        string? json = sessionAttributes![AttrKey]?.ToString();
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var state = JsonConvert.DeserializeObject<PaginationState>(json);
            return state?.ItemIds != null && state.ItemIds.Length > 0 ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Write pagination state to a dictionary (for setting on response.SessionAttributes).
    /// </summary>
    public static void WriteState(Dictionary<string, object> attributes, ListType type, string[] itemIds, int currentOffset, int pageSize)
    {
        var state = new PaginationState
        {
            Type = type,
            ItemIds = itemIds,
            CurrentOffset = currentOffset,
            PageSize = pageSize
        };
        attributes[AttrKey] = JsonConvert.SerializeObject(state);
    }

    /// <summary>
    /// Remove pagination state from the attributes dictionary.
    /// </summary>
    public static void ClearState(Dictionary<string, object> attributes)
    {
        attributes.Remove(AttrKey);
    }

    /// <summary>
    /// Build the next-page SkillResponse from stored pagination state.
    /// Resolves only the item IDs needed for the current page (not all stored IDs).
    /// Shared by ShowMoreIntentHandler and YesIntentHandler pagination delegation.
    /// </summary>
    /// <param name="libraryManager">Library manager for resolving item IDs.</param>
    /// <param name="paginationState">The current pagination state.</param>
    /// <param name="locale">The user's locale.</param>
    /// <returns>A SkillResponse with the next page of items, or a "no more items" message.</returns>
    public static SkillResponse BuildNextPageResponse(
        ILibraryManager libraryManager,
        PaginationState paginationState,
        string locale)
    {
        if (paginationState.CurrentOffset >= paginationState.ItemIds.Length)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoMoreItems", locale));
        }

        // Resolve only the items for this page, not all stored IDs
        int remaining = paginationState.ItemIds.Length - paginationState.CurrentOffset;
        int voiceCount = Math.Min(remaining, paginationState.PageSize);
        var names = new List<string>(voiceCount);

        for (int i = 0; i < voiceCount; i++)
        {
            string idStr = paginationState.ItemIds[paginationState.CurrentOffset + i];
            if (Guid.TryParse(idStr, out Guid id))
            {
                var item = libraryManager.GetItemById(id);
                if (item != null)
                {
                    names.Add(BaseHandler.EscapeXml(item.Name ?? string.Empty));
                }
            }
        }

        if (names.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoMoreItems", locale));
        }

        int newOffset = paginationState.CurrentOffset + voiceCount;
        bool hasMore = newOffset < paginationState.ItemIds.Length;

        string voiceListText = string.Join(". ", names);
        string speech = hasMore
            ? ResponseStrings.Get("ShowMorePage", locale, voiceListText) + " " + ResponseStrings.Get("ShowMorePrompt", locale)
            : ResponseStrings.Get("ShowMoreLastPage", locale, voiceListText);

        SkillResponse response = hasMore
            ? ResponseBuilder.Ask(speech, new Reprompt(ResponseStrings.Get("ShowMorePrompt", locale)))
            : ResponseBuilder.Tell(speech);

        if (hasMore)
        {
            response.SessionAttributes = new Dictionary<string, object>();
            WriteState(response.SessionAttributes, paginationState.Type, paginationState.ItemIds, newOffset, paginationState.PageSize);
        }

        return response;
    }
}

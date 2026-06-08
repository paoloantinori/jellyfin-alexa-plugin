#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Multi-turn conversational song search handler.
/// Guides the user through providing artist name and/or song title keywords,
/// then searches the Jellyfin library and presents disambiguation results.
/// </summary>
public class FindSongIntentHandler : BaseHandler
{
    private const string SessionDataKey = "FindSongSessionData";

    private static readonly string[] OrdinalWords = new[]
    {
        "one", "two", "three", "four", "uno", "due", "tre", "quattro",
        "eins", "zwei", "drei", "vier", "un", "deux", "trois", "quatre"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly ISongNgramIndex? _songNgramIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="FindSongIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    /// <param name="songNgramIndex">Optional in-memory song n-gram index for fast partial-title lookup.</param>
    public FindSongIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        ISongNgramIndex? songNgramIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _artistIndex = artistIndex;
        _songNgramIndex = songNgramIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        if (request is not IntentRequest intentRequest)
        {
            return false;
        }

        string intentName = intentRequest.Intent.Name;

        if (string.Equals(intentName, IntentNames.FindSongIntent, StringComparison.Ordinal)
            || string.Equals(intentName, IntentNames.FindSongByArtistIntent, StringComparison.Ordinal))
        {
            return true;
        }

        // FallbackIntent is handled when we have active FindSong session state
        if (string.Equals(intentName, IntentNames.AmazonFallback, StringComparison.Ordinal))
        {
            // We cannot read session attributes from CanHandle since the request context
            // doesn't carry session attributes at this point. The pipeline passes them
            // to HandleAsync. So we accept FallbackIntent here and check state inside.
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handle without session attributes — delegates to the session-aware overload.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        return HandleAsync(request, context, user, session, null, cancellationToken);
    }

    /// <summary>
    /// Handle with session attributes for multi-turn FindSong dialogue.
    /// </summary>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        var intentRequest = (IntentRequest)request;

        Logger.LogDebug("FindSong: entered, intent={Intent}, locale={Locale}", intentRequest.Intent.Name, locale);

        // Read existing session state
        FindSongSessionData? sessionData = ReadSessionData(sessionAttributes);

        // If FallbackIntent but no active FindSong session, return the standard
        // fallback response (same as FallbackIntentHandler) since we intercepted it.
        if (sessionData == null
            && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonFallback, StringComparison.Ordinal))
        {
            Logger.LogDebug("FindSong: FallbackIntent without active session, returning standard fallback");
            return ResponseBuilder.Tell(ResponseStrings.Get("CouldNotUnderstand", locale));
        }

        // If we have session data and the intent is FindSongIntent, it could be
        // a fresh invocation or a continuation. Check for slot values to distinguish.
        if (sessionData == null)
        {
            return await HandleFirstInvocationAsync(request, context, user, session, locale, cancellationToken).ConfigureAwait(false);
        }

        return sessionData.State switch
        {
            FindSongState.AwaitingArtist => await HandleAwaitingArtistAsync(request, context, user, session, locale, sessionData, cancellationToken).ConfigureAwait(false),
            FindSongState.AwaitingKeywords => await HandleAwaitingKeywordsAsync(request, context, user, session, locale, sessionData, cancellationToken).ConfigureAwait(false),
            FindSongState.Disambiguating => await HandleDisambiguatingAsync(request, context, user, session, locale, sessionData, cancellationToken).ConfigureAwait(false),
            _ => await HandleFirstInvocationAsync(request, context, user, session, locale, cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// First invocation with no session state. Determine which piece of info we have
    /// and prompt for the missing piece using Dialog.ElicitSlot so Alexa captures the
    /// user's reply as a slot value rather than routing it through NLU (which would
    /// send short replies to FallbackIntent with no slots).
    /// When the artist is provided upfront, resolves the artist ID immediately so
    /// the search is artist-scoped and doesn't need to re-ask for the artist later.
    /// </summary>
    private async Task<SkillResponse> HandleFirstInvocationAsync(Request request, Context context, Entities.User user, SessionInfo session, string locale, CancellationToken cancellationToken)
    {
        var intentRequest = (IntentRequest)request;

        string? musician = GetSlotValue(intentRequest, "musician");
        string? titleKeywords = GetSlotValue(intentRequest, "titleKeywords");

        FindSongSessionData sessionData = new();

        if (!string.IsNullOrWhiteSpace(musician))
        {
            // Artist provided — resolve to a Jellyfin ID now so the search
            // is artist-scoped and we don't need to re-ask for the artist.
            string artistInput = musician.Trim();
            sessionData.ArtistName = artistInput;
            sessionData.State = FindSongState.AwaitingKeywords;

            IReadOnlyList<BaseItem> artists = await ArtistSearch.SearchAsync(
                artistInput, user, _libraryManager, _artistIndex, Logger,
                (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", ct),
                cancellationToken).ConfigureAwait(false);

            if (artists.Count > 0)
            {
                sessionData.ArtistId = artists[0].Id;
                sessionData.ArtistName = artists[0].Name;
                Logger.LogDebug("FindSong: resolved artist '{Input}' to '{Name}' (Id={Id})", artistInput, artists[0].Name, artists[0].Id);
            }
            else
            {
                Logger.LogDebug("FindSong: could not resolve artist '{Input}', will search without artist filter", artistInput);
            }

            return ElicitTitleKeywords(
                ResponseStrings.Get("FindSongPromptKeywords", locale),
                sessionData);
        }

        if (!string.IsNullOrWhiteSpace(titleKeywords))
        {
            // Keywords provided, need artist
            sessionData.Keywords = titleKeywords.Trim();
            sessionData.State = FindSongState.AwaitingArtist;
            return ElicitArtist(
                ResponseStrings.Get("FindSongPromptArtist", locale),
                sessionData);
        }

        // Neither provided, prompt for keywords first
        sessionData.State = FindSongState.AwaitingKeywords;
        return ElicitTitleKeywords(
            ResponseStrings.Get("FindSongPromptKeywords", locale),
            sessionData);
    }

    /// <summary>
    /// Handle the AwaitingArtist state — user is providing an artist name.
    /// </summary>
    private async Task<SkillResponse> HandleAwaitingArtistAsync(Request request, Context context, Entities.User user, SessionInfo session, string locale, FindSongSessionData sessionData, CancellationToken cancellationToken)
    {
        var intentRequest = (IntentRequest)request;

        // Try to get artist from slot, otherwise use raw transcript
        string? musician = GetSlotValue(intentRequest, "musician");
        string? transcript = GetSlotValue(intentRequest, "titleKeywords");

        // Fallback: extract text from any slot when NLU misroutes to unexpected intent
        string? anySlot = GetAnySlotValue(intentRequest);

        string? artistInput = !string.IsNullOrWhiteSpace(musician) ? musician.Trim()
            : !string.IsNullOrWhiteSpace(transcript) ? transcript.Trim()
            : !string.IsNullOrWhiteSpace(anySlot) ? anySlot.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(artistInput))
        {
            return ElicitArtist(ResponseStrings.Get("FindSongPromptArtist", locale),
                sessionData);
        }

        // Resolve the artist
        IReadOnlyList<BaseItem> artists = await ArtistSearch.SearchAsync(
            artistInput, user, _libraryManager, _artistIndex, Logger,
            (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", ct),
            cancellationToken).ConfigureAwait(false);

        if (artists.Count == 0)
        {
            string notFoundMsg = ResponseStrings.Get("FindSongArtistNotFound", locale, artistInput);
            return ElicitArtist(notFoundMsg,
                sessionData);
        }

        // Artist found — store it and proceed to search
        sessionData.ArtistId = artists[0].Id;
        sessionData.ArtistName = artists[0].Name;

        return await SearchAndRespondAsync(request, context, user, session, locale, sessionData, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handle the AwaitingKeywords state — user is providing song title keywords.
    /// </summary>
    private async Task<SkillResponse> HandleAwaitingKeywordsAsync(Request request, Context context, Entities.User user, SessionInfo session, string locale, FindSongSessionData sessionData, CancellationToken cancellationToken)
    {
        var intentRequest = (IntentRequest)request;

        // Try to get keywords from slot or transcript
        string? keywords = GetSlotValue(intentRequest, "titleKeywords");

        if (string.IsNullOrWhiteSpace(keywords))
        {
            // Try musician slot as fallback (Alexa might route there)
            keywords = GetSlotValue(intentRequest, "musician");
        }

        if (string.IsNullOrWhiteSpace(keywords))
        {
            // Fallback: extract text from any slot in the intent. When Alexa NLU
            // misroutes the user's short reply (e.g. "family") to an unexpected intent
            // like BrowseLibraryIntent, the text may end up in that intent's slots.
            keywords = GetAnySlotValue(intentRequest);
        }

        if (string.IsNullOrWhiteSpace(keywords))
        {
            return ElicitTitleKeywords(ResponseStrings.Get("FindSongPromptKeywords", locale),
                sessionData);
        }

        // Tokenize and check for stop-words-only
        string[] tokens = KeywordMatcher.Tokenize(keywords, locale);

        if (tokens.Length == 0)
        {
            return ElicitTitleKeywords(ResponseStrings.Get("FindSongTooVague", locale),
                sessionData);
        }

        // Store keywords and proceed to search
        sessionData.Keywords = keywords.Trim();

        // Search with or without artist filter
        return await SearchAndRespondAsync(request, context, user, session, locale, sessionData, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handle the Disambiguating state — user is picking from the candidate list.
    /// </summary>
    private async Task<SkillResponse> HandleDisambiguatingAsync(Request request, Context context, Entities.User user, SessionInfo session, string locale, FindSongSessionData sessionData, CancellationToken cancellationToken)
    {
        var intentRequest = (IntentRequest)request;

        if (sessionData.Candidates == null || sessionData.Candidates.Count == 0)
        {
            // No candidates — reset and start over
            Logger.LogDebug("FindSong: Disambiguating state with no candidates, restarting");
            return await HandleFirstInvocationAsync(request, context, user, session, locale, cancellationToken).ConfigureAwait(false);
        }

        // Extract the user's input
        string? input = GetSlotValue(intentRequest, "titleKeywords")
            ?? GetSlotValue(intentRequest, "musician")
            ?? GetAnySlotValue(intentRequest);

        // If no slot, try the raw intent transcript
        if (string.IsNullOrWhiteSpace(input))
        {
            // Cannot determine pick — ask again
            string invalidMsg = ResponseStrings.Get("FindSongInvalidPick", locale);
            return ElicitTitleKeywords(invalidMsg,
                sessionData);
        }

        input = input.Trim();

        // Try to match by number, ordinal, or partial title
        int? pickIndex = ResolvePick(input, sessionData.Candidates, locale);

        if (!pickIndex.HasValue || pickIndex.Value < 0 || pickIndex.Value >= sessionData.Candidates.Count)
        {
            string invalidMsg = ResponseStrings.Get("FindSongInvalidPick", locale);
            return ElicitTitleKeywords(invalidMsg,
                sessionData);
        }

        FindSongCandidate picked = sessionData.Candidates[pickIndex.Value];

        Logger.LogDebug("FindSong: disambiguation picked candidate #{Index}: {Name} (Id={Id})", pickIndex.Value, picked.Name, picked.ItemId);

        // Resolve the Jellyfin user for playback
        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // Get the full item for metadata
        BaseItem? item = _libraryManager.GetItemById(picked.ItemId);
        if (item == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByName", locale, picked.Name));
        }

        string itemId = item.Id.ToString();
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = item.Id } };
        session.FullNowPlayingItem = item;

        string artistDisplay = picked.ArtistName ?? sessionData.ArtistName ?? "Unknown";
        string announcement = ResponseStrings.Get("FindSongFoundOne", locale, item.Name, artistDisplay);
        SkillResponse playResponse = BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, item, user, context);
        playResponse.Response.OutputSpeech = new PlainTextOutputSpeech { Text = announcement };
        return playResponse;
    }

    /// <summary>
    /// Search for songs using the collected session data and return the appropriate response.
    /// </summary>
    private async Task<SkillResponse> SearchAndRespondAsync(Request request, Context context, Entities.User user, SessionInfo session, string locale, FindSongSessionData sessionData, CancellationToken cancellationToken)
    {
        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        string[] keywordTokens = KeywordMatcher.Tokenize(sessionData.Keywords, locale);

        if (keywordTokens.Length == 0)
        {
            string vagueMsg = ResponseStrings.Get("FindSongTooVague", locale);
            return ElicitTitleKeywords(vagueMsg,
                sessionData);
        }

        List<BaseItem> songs;
        List<(BaseItem Item, double Score)> scored;

        if (sessionData.ArtistId.HasValue)
        {
            // Artist-scoped search: use ArtistIds + NameContains filter
            var artistQuery = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                ArtistIds = new[] { sessionData.ArtistId.Value },
                NameContains = keywordTokens[0],
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(artistQuery, user, _libraryManager);

            IReadOnlyList<BaseItem> allArtistSongs = await RetryAsync(() => _libraryManager.GetItemList(artistQuery), "GetSongsByArtist", cancellationToken).ConfigureAwait(false);

            // Post-filter with KeywordMatcher
            scored = KeywordMatcher.Score(allArtistSongs, keywordTokens, locale);
            songs = scored.Select(s => s.Item).ToList();
        }
        else
        {
            // Try n-gram index first (O(1) lookup), fall back to DB query if unavailable
            Guid[]? topParentIds = GetAllowedLibraryIds(user);
            if (_songNgramIndex is { IsReady: true })
            {
                Logger.LogDebug("FindSong: searching n-gram index (keywords={Keywords})", string.Join(" ", keywordTokens));
                scored = _songNgramIndex.Search(keywordTokens, locale, topParentIds);
            }
            else
            {
                scored = new List<(BaseItem, double)>();
            }

            if (scored.Count == 0)
            {
                // Fallback: DB NameContains query + KeywordMatcher post-filter
                Logger.LogDebug("FindSong: n-gram index miss or unavailable, falling back to DB query");
                string firstToken = keywordTokens[0];
                var nameQuery = new InternalItemsQuery
                {
                    User = jellyfinUser,
                    Recursive = true,
                    NameContains = firstToken,
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    DtoOptions = new DtoOptions(true)
                };
                ApplyLibraryFilter(nameQuery, user, _libraryManager);

                IReadOnlyList<BaseItem> nameMatches = await RetryAsync(() => _libraryManager.GetItemList(nameQuery), "GetSongsByNameContains", cancellationToken).ConfigureAwait(false);

                // Post-filter with KeywordMatcher
                scored = KeywordMatcher.Score(nameMatches, keywordTokens, locale);
            }

            songs = scored.Select(s => s.Item).ToList();
        }

        Logger.LogDebug("FindSong: search returned {Count} matching songs (artist={ArtistName}, keywords={Keywords})", songs.Count, sessionData.ArtistName, sessionData.Keywords);

        // No matches
        if (songs.Count == 0)
        {
            string noMatchMsg = ResponseStrings.Get("FindSongNoMatch", locale);
            return ElicitTitleKeywords(noMatchMsg,
                sessionData);
        }

        // Single match — auto-play
        if (songs.Count == 1)
        {
            BaseItem song = songs[0];
            string itemId = song.Id.ToString();

            session.NowPlayingQueue = new List<QueueItem> { new() { Id = song.Id } };
            session.FullNowPlayingItem = song;

            string artistDisplay = sessionData.ArtistName ?? "Unknown";
            string announcement = ResponseStrings.Get("FindSongFoundOne", locale, song.Name, artistDisplay);
            SkillResponse singleResponse = BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, song, user, context);
            singleResponse.Response.OutputSpeech = new PlainTextOutputSpeech { Text = announcement };
            return singleResponse;
        }

        // Multiple matches: take top 4 and check if we need artist narrowing
        List<BaseItem> topSongs = songs.Take(4).ToList();

        if (songs.Count > 4 && !sessionData.ArtistId.HasValue)
        {
            // Too many results without artist filter — ask for artist
            string narrowMsg = ResponseStrings.Get("FindSongTooManyNarrow", locale);
            sessionData.State = FindSongState.AwaitingArtist;
            sessionData.Candidates = null;

            return ElicitArtist(narrowMsg,
                sessionData);
        }

        // 1-4 matches — present disambiguation list with real scores
        var disambigCandidates = scored.Take(4)
            .Select(s => new FindSongCandidate(s.Item.Id, s.Item.Name, null, s.Score))
            .ToList();

        sessionData.State = FindSongState.Disambiguating;
        sessionData.Candidates = disambigCandidates;

        // Build the list announcement
        var candidateNames = string.Join(", ", disambigCandidates.Select((c, i) => $"{i + 1}. {c.Name}"));
        string foundMultipleMsg = ResponseStrings.Get("FindSongFoundMultiple", locale, disambigCandidates.Count, candidateNames);
        string fullPrompt = $"{foundMultipleMsg} {candidateNames}";

        return ElicitTitleKeywords(fullPrompt,
            sessionData);
    }

    /// <summary>
    /// Resolve which candidate the user picked by number, ordinal word, or partial title match.
    /// Returns a 0-based index, or null if no match.
    /// </summary>
    internal static int? ResolvePick(string input, List<FindSongCandidate> candidates, string locale)
    {
        if (string.IsNullOrWhiteSpace(input) || candidates.Count == 0)
        {
            return null;
        }

        string trimmed = input.Trim();

        // 1. Try numeric match: "1", "2", "the first one", "the second one", etc.
        int? numericPick = TryParseNumericPick(trimmed);
        if (numericPick.HasValue)
        {
            return numericPick;
        }

        // 2. Try ordinal words: "one", "two", "three", "four"
        int? ordinalPick = TryParseOrdinalWord(trimmed);
        if (ordinalPick.HasValue)
        {
            return ordinalPick;
        }

        // 3. Try partial title match against candidate names
        return TryMatchByTitle(trimmed, candidates);
    }

    private static int? TryParseNumericPick(string input)
    {
        // Direct number: "1", "2", etc.
        if (int.TryParse(input, out int num) && num >= 1 && num <= 4)
        {
            return num - 1;
        }

        // English ordinals: "the first one", "the second one", "the third one", "the fourth one"
        // Italian: "il primo", "il secondo", "il terzo", "il quarto"
        // German: "der erste", "der zweite", "der dritte", "der vierte"
        string lower = input.ToLowerInvariant();

        if (lower.Contains("first", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("primo", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("erste", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (lower.Contains("second", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("secondo", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("zweite", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (lower.Contains("third", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("terzo", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("dritte", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (lower.Contains("fourth", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("quarto", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("vierte", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return null;
    }

    private static int? TryParseOrdinalWord(string input)
    {
        string lower = input.ToLowerInvariant().Trim();

        for (int i = 0; i < OrdinalWords.Length; i++)
        {
            if (string.Equals(lower, OrdinalWords[i], StringComparison.OrdinalIgnoreCase))
            {
                // Map: one->0, two->1, three->2, four->3
                // Words are grouped in sets of 4 (English, Italian, German, French)
                int ordinalIndex = i % 4;
                return ordinalIndex;
            }
        }

        return null;
    }

    private static int? TryMatchByTitle(string input, List<FindSongCandidate> candidates)
    {
        string lower = input.ToLowerInvariant();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (!string.IsNullOrEmpty(candidates[i].Name)
                && candidates[i].Name!.Contains(lower, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        // Also try: does the input contain words from the candidate name?
        var inputWords = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (inputWords.Length > 0)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.IsNullOrEmpty(candidates[i].Name))
                {
                    continue;
                }

                string candidateLower = candidates[i].Name!.ToLowerInvariant();
                if (inputWords.Any(w => candidateLower.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    return i;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extract a slot value from the intent request, or null if the slot is missing/empty.
    /// </summary>
    private static string? GetSlotValue(IntentRequest intentRequest, string slotName)
    {
        if (intentRequest.Intent.Slots == null)
        {
            return null;
        }

        if (!intentRequest.Intent.Slots.TryGetValue(slotName, out var slot))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(slot.Value) ? null : slot.Value;
    }

    /// <summary>
    /// Extract text from any slot in the intent, used as a fallback when the expected
    /// slots (titleKeywords, musician) are not present. This handles the case where
    /// Alexa NLU routes the user's reply to an unexpected intent (e.g. ShowMoreIntent
    /// or BrowseLibraryIntent) during a multi-turn FindSong dialog.
    /// </summary>
    internal static string? GetAnySlotValue(IntentRequest intentRequest)
    {
        if (intentRequest.Intent.Slots == null || intentRequest.Intent.Slots.Count == 0)
        {
            return null;
        }

        // Return the first non-empty slot value
        foreach (var slot in intentRequest.Intent.Slots.Values)
        {
            if (!string.IsNullOrWhiteSpace(slot.Value))
            {
                return slot.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Read FindSongSessionData from Alexa session attributes.
    /// </summary>
    internal static FindSongSessionData? ReadSessionData(Dictionary<string, object>? sessionAttributes)
    {
        if (sessionAttributes == null)
        {
            return null;
        }

        if (!sessionAttributes.TryGetValue(SessionDataKey, out var value))
        {
            return null;
        }

        string? json = value?.ToString();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<FindSongSessionData>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Build session attributes dictionary with the serialized FindSongSessionData.
    /// </summary>
    private static Dictionary<string, object> BuildSessionAttributes(FindSongSessionData sessionData)
    {
        return new Dictionary<string, object>
        {
            [SessionDataKey] = JsonConvert.SerializeObject(sessionData)
        };
    }

    /// <summary>
    /// Prompt the user for song title keywords using Dialog.ElicitSlot.
    /// Alexa captures the next utterance directly into the titleKeywords slot.
    /// </summary>
    private static SkillResponse ElicitTitleKeywords(string prompt, FindSongSessionData sessionData)
        => BuildElicitSlotResponse(IntentNames.Slots.TitleKeywords, IntentNames.FindSongIntent, prompt, sessionData);

    /// <summary>
    /// Prompt the user for an artist name using Dialog.ElicitSlot.
    /// Uses AMAZON.SearchQuery (titleKeywords slot) instead of AMAZON.Musician because
    /// built-in entity types like AMAZON.Musician validate the captured text against Amazon's
    /// database — indie/obscure artists fail resolution, causing Alexa to route the utterance
    /// to general NLU (Netflix, Amazon Music, etc.) instead of filling the slot.
    /// AMAZON.SearchQuery accepts any free-form text without entity validation.
    /// The handler extracts the text from titleKeywords in HandleAwaitingArtistAsync.
    /// </summary>
    private static SkillResponse ElicitArtist(string prompt, FindSongSessionData sessionData)
        => BuildElicitSlotResponse(IntentNames.Slots.TitleKeywords, IntentNames.FindSongIntent, prompt, sessionData);

    /// <summary>
    /// Build a response that uses Dialog.ElicitSlot to capture the user's next utterance
    /// as a specific slot value. Always targets the titleKeywords slot (AMAZON.SearchQuery)
    /// on FindSongIntent, regardless of whether we're asking for keywords or artist name.
    /// AMAZON.SearchQuery accepts any free-form text without built-in entity validation,
    /// which prevents Alexa from routing the utterance to other skills when the artist
    /// name isn't in Amazon's entity database (e.g. indie bands).
    /// The handler differentiates between keywords and artist based on session state.
    /// </summary>
    private static SkillResponse BuildElicitSlotResponse(
        string slotName,
        string intentName,
        string prompt,
        FindSongSessionData sessionData)
    {
        return new SkillResponse
        {
            Version = "1.0",
            SessionAttributes = BuildSessionAttributes(sessionData),
            Response = new ResponseBody
            {
                ShouldEndSession = false,
                OutputSpeech = new PlainTextOutputSpeech { Text = prompt },
                Reprompt = new Reprompt(prompt),
                Directives = new List<IDirective>
                {
                    new ElicitSlotDirective(slotName, intentName)
                }
            }
        };
    }
}

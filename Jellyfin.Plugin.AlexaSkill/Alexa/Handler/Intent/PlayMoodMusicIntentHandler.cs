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
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayMoodMusicIntent requests. Plays music matching a mood by mapping
/// mood keywords to genres and querying Audio items.
/// </summary>
public class PlayMoodMusicIntentHandler : BaseHandler
{
    private static readonly Dictionary<string, string[]> MoodGenreMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["relaxing"] = new[] { "ambient", "acoustic", "jazz", "classical", "new age" },
        ["chill"] = new[] { "chillout", "ambient", "lounge", "downtempo" },
        ["upbeat"] = new[] { "pop", "rock", "dance", "electronic" },
        ["energetic"] = new[] { "rock", "electronic", "metal", "punk" },
        ["focus"] = new[] { "classical", "ambient", "instrumental" },
        ["romantic"] = new[] { "r&b", "jazz", "soul", "pop" },
        ["happy"] = new[] { "pop", "dance", "reggae" },
        ["sad"] = new[] { "blues", "indie", "folk", "alternative" },
        ["party"] = new[] { "dance", "electronic", "hip hop", "pop" },
        ["workout"] = new[] { "electronic", "rock", "hip hop", "metal" },
        ["morning"] = new[] { "acoustic", "pop", "indie", "folk", "jazz" },
        ["evening"] = new[] { "jazz", "ambient", "classical", "lounge", "soul" },
        ["dinner"] = new[] { "jazz", "classical", "acoustic", "bossa nova", "soul" }
    };

    /// <summary>
    /// Maps localized mood words to their English MoodGenreMap keys.
    /// This allows non-English users to use native mood words that resolve to
    /// the same genre arrays as their English counterparts.
    /// Covers: it-IT, de-DE, es-ES, fr-FR, pt-BR.
    /// Entries are sorted alphabetically by key. Shared words across locales
    /// (e.g. "triste" in it-IT/es-ES/fr-FR/pt-BR) appear once since all map
    /// to the same English key and the dictionary uses OrdinalIgnoreCase.
    /// </summary>
    private static readonly Dictionary<string, string> LocalizedMoodMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // de-DE
        ["abend"] = "evening",
        ["abends"] = "evening",
        ["abendessen"] = "dinner",

        // de-DE
        ["beruhigend"] = "chill",
        ["beschwingt"] = "upbeat",

        // it-IT
        ["allenamento"] = "workout",
        ["allegra"] = "happy",
        ["allegramente"] = "happy",

        // es-ES + pt-BR
        ["alegre"] = "happy",

        // es-ES + pt-BR
        ["animada"] = "upbeat",

        // es-ES + pt-BR
        ["animado"] = "upbeat",

        // it-IT + pt-BR
        ["calma"] = "chill",

        // pt-BR
        ["calmo"] = "chill",

        // it-IT + es-ES
        ["cena"] = "dinner",

        // fr-FR
        ["concentration"] = "focus",

        // es-ES
        ["concentración"] = "focus",

        // pt-BR
        ["concentração"] = "focus",

        // it-IT
        ["concentrazione"] = "focus",

        // it-IT (compound moods)
        ["da allenamento"] = "workout",
        ["da cena"] = "dinner",
        ["da festa"] = "party",

        // fr-FR
        ["détendant"] = "relaxing",
        ["détendu"] = "relaxing",
        ["détendue"] = "relaxing",
        ["dîner"] = "dinner",
        ["dynamique"] = "upbeat",

        // fr-FR
        ["énergique"] = "energetic",

        // es-ES
        ["enérgica"] = "energetic",
        ["enérgico"] = "energetic",
        ["entrenamiento"] = "workout",

        // fr-FR
        ["entraînement"] = "workout",

        // de-DE
        ["energisch"] = "energetic",
        ["entspannend"] = "relaxing",
        ["entspannt"] = "relaxing",

        // pt-BR
        ["energética"] = "energetic",
        ["energético"] = "energetic",
        ["exercício"] = "workout",

        // de-DE
        ["feier"] = "party",

        // es-ES + pt-BR
        ["feliz"] = "happy",

        // it-IT + pt-BR
        ["festa"] = "party",

        // es-ES
        ["fiesta"] = "party",

        // pt-BR
        ["foco"] = "focus",

        // de-DE
        ["fokus"] = "focus",
        ["fröhlich"] = "happy",
        ["glücklich"] = "happy",

        // fr-FR
        ["heureuse"] = "happy",
        ["heureux"] = "happy",

        // fr-FR
        ["joyeuse"] = "happy",
        ["joyeux"] = "happy",

        // pt-BR
        ["jantar"] = "dinner",

        // pt-BR
        ["manhã"] = "morning",

        // fr-FR + pt-BR (shared word, one entry covers both)
        ["matinal"] = "morning",

        // it-IT + es-ES
        ["mattutina"] = "morning",

        // es-ES
        ["matutino"] = "morning",

        // de-DE
        ["morgens"] = "morning",

        // es-ES
        ["nocturna"] = "evening",
        ["nocturno"] = "evening",

        // pt-BR
        ["noite"] = "evening",
        ["noturna"] = "evening",
        ["noturno"] = "evening",

        // de-DE
        ["party"] = "party",

        // fr-FR
        ["reposant"] = "chill",

        // es-ES
        ["relajado"] = "chill",
        ["relajante"] = "relaxing",

        // pt-BR
        ["relaxada"] = "relaxing",
        ["relaxado"] = "relaxing",

        // it-IT + pt-BR
        ["relaxante"] = "relaxing",

        // it-IT
        ["rilassante"] = "relaxing",

        // de-DE
        ["romantisch"] = "romantic",

        // it-IT
        ["romantica"] = "romantic",
        ["romantico"] = "romantic",

        // es-ES
        ["romántica"] = "romantic",
        ["romántico"] = "romantic",

        // fr-FR
        ["romantique"] = "romantic",

        // pt-BR
        ["romântica"] = "romantic",
        ["romântico"] = "romantic",

        // it-IT
        ["serale"] = "evening",

        // fr-FR
        ["soirée"] = "evening",

        // es-ES
        ["tranquila"] = "relaxing",
        ["tranquilo"] = "relaxing",

        // de-DE
        ["training"] = "workout",
        ["traurig"] = "sad",

        // pt-BR
        ["treino"] = "workout",

        // it-IT + es-ES + fr-FR + pt-BR
        ["triste"] = "sad",

        // it-IT
        ["tristezza"] = "sad"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayMoodMusicIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayMoodMusicIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayMoodMusic, StringComparison.Ordinal);
    }

    /// <summary>
    /// Play music matching the requested mood.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? mood = null;
        if (intentRequest.Intent.Slots != null && intentRequest.Intent.Slots.TryGetValue("mood", out Slot? moodSlot))
        {
            mood = moodSlot.Value;
        }

        if (string.IsNullOrEmpty(mood))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchMood", locale));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        string[] genres = ResolveGenres(mood, DateTime.Now.Hour);
        Logger.LogDebug("PlayMoodMusic: mood='{Mood}', resolved genres=[{Genres}]", mood, string.Join(", ", genres));

        List<BaseItem> foundItems = new();

        foreach (string genre in genres)
        {
            var query = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Genres = new[] { genre },
                Limit = 100,
                OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(query, user, _libraryManager);

            IReadOnlyList<BaseItem> items = await RetryAsync(() => _libraryManager.GetItemList(query), "GetMoodItems", cancellationToken).ConfigureAwait(false);

            if (items.Count > 0)
            {
                foundItems.AddRange(items);
                break;
            }
        }

        if (foundItems.Count == 0)
        {
            Logger.LogDebug("PlayMoodMusic: no tracks found by genre, trying artist-genre fallback");
            foundItems = await SearchByArtistGenreAsync(genres, jellyfinUser!, user, cancellationToken).ConfigureAwait(false);
        }

        if (foundItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundMood", locale, mood));
        }

        BaseItem selected = foundItems[Random.Shared.Next(foundItems.Count)];

        List<QueueItem> queueItems = new List<QueueItem>
        {
            new() { Id = selected.Id }
        };

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = selected;

        string itemId = selected.Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, selected, user, context);
    }

    /// <summary>
    /// Resolves a mood string to an array of genre names.
    /// Tries an exact match first, then a contains match, then falls back to
    /// using the mood word itself as a genre. Reorders genres based on time of day.
    /// </summary>
    /// <param name="mood">The mood keyword from the user.</param>
    /// <param name="hour">Current hour (0-23) for time-of-day bias.</param>
    /// <returns>An array of genre names to search for.</returns>
    internal static string[] ResolveGenres(string mood, int hour = -1)
    {
        string[]? genres = null;

        // 1. Exact match against English mood keys
        if (MoodGenreMap.TryGetValue(mood, out string[]? mapped))
        {
            genres = mapped;
        }

        // 2. Translate localized mood word to English key, then look up
        if (genres == null && LocalizedMoodMap.TryGetValue(mood, out string? englishKey))
        {
            if (MoodGenreMap.TryGetValue(englishKey, out string[]? localizedMapped))
            {
                genres = localizedMapped;
            }
        }

        // 3. Substring match against English mood keys
        if (genres == null)
        {
            foreach (KeyValuePair<string, string[]> entry in MoodGenreMap)
            {
                if (mood.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    genres = entry.Value;
                    break;
                }
            }
        }

        // 4. Substring match against localized mood keys
        if (genres == null)
        {
            foreach (KeyValuePair<string, string> entry in LocalizedMoodMap)
            {
                if (mood.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    if (MoodGenreMap.TryGetValue(entry.Value, out string[]? subMapped))
                    {
                        genres = subMapped;
                        break;
                    }
                }
            }
        }

        // 5. Fallback: use raw mood as genre
        if (genres == null)
        {
            return new[] { mood };
        }

        if (hour < 0)
        {
            return genres;
        }

        // Bias genre order toward time-appropriate genres
        string[] preferred = GetTimePreferredGenres(hour);
        return genres
            .OrderByDescending(g => preferred.Contains(g, StringComparer.OrdinalIgnoreCase))
            .ThenBy(_ => Random.Shared.Next())
            .ToArray();
    }

    /// <summary>
    /// Fallback search: finds artists tagged with the given genres, then collects
    /// their audio tracks. This handles the common case where Jellyfin tags genres
    /// at the artist level but not at the individual track level.
    /// </summary>
    /// <param name="genres">Genre names to search for.</param>
    /// <param name="jellyfinUser">The Jellyfin user for query context.</param>
    /// <param name="user">The plugin user for library filtering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of audio tracks from matching artists (up to 100).</returns>
    private async Task<List<BaseItem>> SearchByArtistGenreAsync(
        string[] genres,
        JellyfinUser jellyfinUser,
        Entities.User user,
        CancellationToken cancellationToken)
    {
        List<BaseItem> tracks = new();

        foreach (string genre in genres)
        {
            var artistQuery = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                Genres = new[] { genre },
                Limit = 20,
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(artistQuery, user, _libraryManager);

            IReadOnlyList<BaseItem> artists = await RetryAsync(
                () => _libraryManager.GetItemList(artistQuery),
                "GetMoodArtists",
                cancellationToken).ConfigureAwait(false);

            Logger.LogDebug("PlayMoodMusic: artist fallback genre='{Genre}' found {ArtistCount} artists", genre, artists.Count);

            foreach (BaseItem artist in artists)
            {
                if (tracks.Count >= 100)
                {
                    break;
                }

                var trackQuery = new InternalItemsQuery
                {
                    User = jellyfinUser,
                    Recursive = true,
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    ArtistIds = new[] { artist.Id },
                    Limit = 100 - tracks.Count,
                    OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                    DtoOptions = new DtoOptions(true)
                };
                ApplyLibraryFilter(trackQuery, user, _libraryManager);

                IReadOnlyList<BaseItem> artistTracks = await RetryAsync(
                    () => _libraryManager.GetItemList(trackQuery),
                    "GetMoodArtistTracks",
                    cancellationToken).ConfigureAwait(false);

                tracks.AddRange(artistTracks);
            }

            if (tracks.Count > 0)
            {
                break;
            }
        }

        Logger.LogDebug("PlayMoodMusic: artist fallback found {TrackCount} total tracks", tracks.Count);
        return tracks;
    }

    private static string[] GetTimePreferredGenres(int hour) => hour switch
    {
        >= 5 and < 10 => new[] { "acoustic", "pop", "folk", "indie", "classical" },
        >= 10 and < 14 => new[] { "pop", "rock", "indie", "alternative" },
        >= 14 and < 18 => new[] { "rock", "electronic", "hip hop", "dance" },
        >= 18 and < 22 => new[] { "jazz", "ambient", "lounge", "soul", "classical" },
        _ => new[] { "ambient", "chillout", "classical", "jazz" }
    };
}

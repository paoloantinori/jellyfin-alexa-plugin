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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaySongIntent requests.
/// </summary>
public class PlaySongIntentHandler : BaseHandler
{
    private static readonly string[] SongCarrierPhrases = new[]
    {
        // Phrases that appear right before {song} in utterance templates.
        // Within each locale group, longer phrases come first (e.g. "the song called " before "the song ").
        // English
        "the song called ", "a song called ",
        "that song ", "the song ", "the track ", "a song ", "a track ",
        // Italian
        "la canzone ", "il brano ", "il pezzo ", "la traccia ",
        "una canzone ", "un brano ", "un pezzo ", "una traccia ",
        "canzone ", "brano ", "pezzo ", "traccia ",
        // German
        "das lied ", "das stück ", "den titel ",
        "ein lied ", "ein stück ",
        // Spanish
        "la canción ", "el tema ", "una canción ", "canción ",
        // French
        "la chanson ", "le titre ", "le morceau ", "une chanson ", "chanson ",
        // Dutch
        "het liedje ", "het nummer ", "liedje ", "nummer ",
        // Portuguese
        "a música ", "a faixa ", "música ",
    };

    private ILibraryManager _libraryManager;
    private IUserManager _userManager;
    private readonly IArtistIndex? _artistIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaySongIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    public PlaySongIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _artistIndex = artistIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlaySong, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play a specific song by name, optionally filtered by artist.
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

        string? songQuery = intentRequest.Intent.Slots?.TryGetValue("song", out var songSlot) == true ? songSlot.Value : null;
        string? musicianQuery = intentRequest.Intent.Slots?.TryGetValue("musician", out var musicianSlot) == true ? musicianSlot.Value : null;

        if (string.IsNullOrWhiteSpace(songQuery))
        {
            return ResponseBuilder.Ask(ResponseStrings.Get("ElicitSongName", locale), new Reprompt(ResponseStrings.Get("ElicitSongName", locale)));
        }

        songQuery = StripSongCarrierPhrase(songQuery);

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        List<Guid> artistsIds = new List<Guid>();
        string? matchedArtistName = null;
        if (!string.IsNullOrWhiteSpace(musicianQuery))
        {
            IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
                musicianQuery, user, _libraryManager, _artistIndex, Logger,
                (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", ct),
                cancellationToken).ConfigureAwait(false);

            if (artists.Count == 0)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByArtist", locale, musicianQuery));
            }

            matchedArtistName = artists[0].Name;
            foreach (BaseItem artist in artists)
            {
                artistsIds.Add(artist.Id);
            }
        }

        var songSearchQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = songQuery,
            ArtistIds = artistsIds.ToArray(),
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(songSearchQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> songs = await RetryAsync(
            () => _libraryManager.GetItemList(songSearchQuery),
            "GetSongs",
            cancellationToken).ConfigureAwait(false);
        if (songs.Count == 0 && !string.IsNullOrWhiteSpace(musicianQuery))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByNameAndArtist", locale, songQuery, matchedArtistName!));
        }
        else if (songs.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByName", locale, songQuery));
        }

        if (songs.Count > 1)
        {
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                songQuery,
                songs,
                s => s.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeSong,
                locale,
                best =>
                {
                    songs = new List<BaseItem> { best };
                    var qi = new List<QueueItem> { new() { Id = best.Id } };
                    session.NowPlayingQueue = qi;
                    session.FullNowPlayingItem = best;
                    string iid = best.Id.ToString();
                    return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(iid, user), iid, best, user, context);
                },
                user: user);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                return missResponse!;
            }

            var matches = songs.Take(3).Select(s => (s.Id, s.Name, (string?)GetImageUrl(s.Id.ToString("N"), user))).ToList();
            return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeSong, locale, context);
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        // Pick the first match
        queueItems.Add(new QueueItem { Id = songs[0].Id });

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = songs[0];

        string item_id = songs[0].Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, songs[0], user, context);
    }

    // Alexa's NLU can misalign slot boundaries, causing carrier phrases like
    // "la canzone" to bleed into the slot value. Strip them before searching.
    internal static string StripSongCarrierPhrase(string query)
    {
        string trimmed = query.TrimStart();
        foreach (string phrase in SongCarrierPhrases)
        {
            if (trimmed.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(phrase.Length).TrimStart();
            }
        }

        return query;
    }
}

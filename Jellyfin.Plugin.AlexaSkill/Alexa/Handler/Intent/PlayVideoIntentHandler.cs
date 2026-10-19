using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayVideoIntent: searches for movies and episodes by title
/// and launches video playback via the Alexa VideoApp interface.
/// JF-874: also serves PlayMovieIntent, the it-IT catalog-backed wrapper twin
/// (its MovieTitle slot is what makes the one-shot wrapper tails selectable;
/// the logic below is shared verbatim, only the title read differs).
/// </summary>
public class PlayVideoIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    // JF-876: the delegation target, REQUIRED like the JF-624 tap-delegation
    // family so a dropped DI registration fails at container construction, not
    // on the first misrouted request.
    private readonly PlaySongIntentHandler _playSongHandler;

    public PlayVideoIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        PlaySongIntentHandler playSongHandler) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _playSongHandler = playSongHandler;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null
            && (string.Equals(intentRequest.Intent.Name, IntentNames.PlayVideo, StringComparison.Ordinal)
                || string.Equals(intentRequest.Intent.Name, IntentNames.PlayMovie, StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        IntentRequest intentRequest = (IntentRequest)request;
        bool isPlayMovie = string.Equals(intentRequest.Intent.Name, IntentNames.PlayMovie, StringComparison.Ordinal);

        // JF-876: the movie catalog's ER pull can select PlayMovieIntent for a
        // SONG carrier ask ("metti il brano X", stable over three scratch
        // builds when the title is also a movie; the live model has not
        // reproduced it, the guard is against the next trainer roll). The raw
        // utterance never reaches the handler, but the slot fill does: the
        // statistical fill swallows the carrier noun (the JF-509 class), so a
        // movie slot whose raw value carries a leading song-carrier phrase
        // ("il brano ", "la canzone ", ...) is a song ask misrouted here.
        // Delegate to the PlaySong handler, so the full song path owns it: the
        // n-gram/keyword search, and on a miss the JF-871 cascade (artist,
        // then album, then video, then the honest not-found). The one-shot
        // request contract (RequestId, Locale, the raw fill on the song slot,
        // the F1 whole-span escape and the F2 single-cut contract) lives on
        // the shared guard family helpers since JF-882. The video-disabled
        // gate below is deliberately AFTER this: the ask is a song ask, the
        // video feature flag must not answer it.
        string? rawMovieFill = GetSlotValue(intentRequest, IntentNames.Slots.Movie);
        if (isPlayMovie
            && MultiValueErDisambiguation.IsSongCarrierMisroute(
                rawMovieFill,
                Util.SlotValueHelper.GetCanonicalValue(intentRequest, IntentNames.Slots.Movie),
                out string detectedSongQuery))
        {
            Logger.LogDebug("PlayMovie: song carrier in movie slot fill, delegating to PlaySong with '{SongQuery}'", detectedSongQuery);
            return await _playSongHandler.HandleAsync(
                MultiValueErDisambiguation.BuildSongDelegation(intentRequest, rawMovieFill!),
                context, user, session, cancellationToken).ConfigureAwait(false);
        }

        string locale = GetLocale(request);

        if (IfFeatureDisabled(c => c.VideoPlaybackEnabled, request) is { } disabled)
        {
            return disabled;
        }

        // The JF-550 hatch (review finding): this handler elicits since the 2026-09-23
        // title-less samples, so it joined the elicitation-trap regime and needs the
        // shared escape legs like its 18 siblings - a captured «ferma» or a trapped
        // full one-shot must end the flow, not search for a movie with that name.
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "PlayVideo") is { } elicitCancel)
        {
            return elicitCancel;
        }

        // JF-874: the title read is intent-shaped. PlayMovieIntent's movie slot
        // is the MovieTitle catalog type, so the ER canonical (the exact library
        // name the catalog synced) wins per the JF-659 contract; the raw fill
        // stays the fallback because the catalog gate does NOT hard-block every
        // miss: a carrier-anchored wrapper whose title matches no catalog value
        // still selects the intent with the span raw-filled (live probe
        // 2026-10-12: 'chiedi a mia collezione di mettere il film ratatouille'
        // arrived with movie='ratatouille' against a seed-only catalog), and a
        // movie added after the last sync must keep playing through that path.
        // PlayVideoIntent's title stays the raw SearchQuery read (no ER there),
        // captured ONCE here: it feeds the query below and the JF-882 miss-tail
        // song-carrier delegation (the free-text slot has no canonical, so no
        // whole-span escape exists there; the raw-first discipline below is
        // that slot's own answer).
        // ACCEPTED (code-review F2, the JF-690 artist precedent): the canonical
        // read takes the ER authority's rank-1 value only, so a multi-value
        // match (a partial-word synonym shared by two movies, reachable since
        // JF-874 widened PartialNameSynonyms to Movie) auto-plays Amazon's
        // first pick with no disambiguation; that residual is CLOSED since
        // JF-877 by the multi-value gate below (the artist path's
        // MultiValueErDisambiguation wiring, movie leg), which asks on two or
        // more distinct library movies. This rank-1 read stays the fallback
        // for every closed-gate leg (JF-702 carries the artist-path
        // residual-site ledger).
        string? rawTitleFill = isPlayMovie ? null : GetSlotValue(intentRequest, "title");
        string? titleQuery = isPlayMovie
            ? Util.SlotValueHelper.GetCanonicalValue(intentRequest, IntentNames.Slots.Movie) ?? rawMovieFill
            : rawTitleFill;

        // JF-509: the AMAZON.SearchQuery title has no catalog to anchor its fill boundary,
        // and the statistical fill drifts between builds: 'voglio guardare il film ada'
        // filled title='film ada' on 2026-09-06 (device corr=655db7c3; the same utterance
        // filled 'ada' the day before). The strip is RAW-FIRST like the album calling-word
        // strip (JF-469): a movie genuinely titled 'Film Stars Don't Die in Liverpool' must
        // stay findable by its raw title, so the raw query runs first and the stripped
        // value is retried only on a confirmed miss (see the search below).

        if (string.IsNullOrWhiteSpace(titleQuery))
        {
            // Live battery 2026-09-23: the title-less ask («riprodurre un film»,
            // stolen from Recommend's greedy media_type by the new title-less
            // samples) ELICITS the title instead of the dead-mic Tell (JF-549
            // class: a question-shaped response that closed the mic).
            // JF-874: a PlayMovieIntent arrival with an empty movie slot elicits
            // through the SAME PlayVideo registration rather than its own: the
            // ElicitSlots table and every locale's dialog.intents stay in
            // validator-enforced lockstep (Phase 8), and a PlayMovie entry would
            // force all 17 locales' dialog sections in the change that
            // deliberately ships it-IT only. The follow-up arrives as
            // PlayVideoIntent and re-enters this handler.
            return BuildDialogElicitResponse("DidNotCatchVideoTitle", locale, "title", IntentNames.PlayVideo, Util.ElicitSlots.For(IntentNames.PlayVideo));
        }

        // JF-643: the title feeds the SearchTerm query, the calling-word-stripped
        // retry, and the fuzzy pass below, all against Latin library names; romanize
        // the query once (a ja-JP title slot arrives as katakana).
        titleQuery = Util.KatakanaRomanizer.Romanize(titleQuery);

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // JF-877 (the JF-690 artist gate's movie leg): the MovieTitle catalog's
        // partial-name synonyms (widened to Movie in JF-874) make a spoken token
        // ER-match SEVERAL movie entries when two movies share a first
        // substantive word (live it-IT 2026-10-19: 'il film gruffalò' returned
        // rank-1 'Il Gruffalò e la sua piccolina', rank-2 'Il Gruffalò', and
        // the rank-1-only read above auto-played the sequel). Resolve EVERY ER
        // canonical against the movie library and, on TWO OR MORE distinct
        // movies, ask which one instead of silently playing Amazon's pick;
        // ALWAYS ask (the artist path's JF-885 rank-1 dominance bypass is
        // deliberately not ported: Amazon ranks the sequel first, a 2s confirm
        // beats a wrong evening film). A collapse to ONE distinct movie adopts
        // the survivor (a stale rank-1 canonical then plays the real movie
        // instead of answering not-found with the survivor sitting resolved);
        // every closed-gate leg (fewer than two ER values, zero resolves)
        // keeps the existing chain below byte-identical.
        List<BaseItem>? erMovies = isPlayMovie
            ? await MultiValueErDisambiguation.ResolveErMovieCandidatesAsync(
                intentRequest, user, jellyfinUser!, _libraryManager, Logger, cancellationToken).ConfigureAwait(false)
            : null;
        if (erMovies is { Count: >= 2 })
        {
            Logger.LogInformation(
                "PlayMovie: the movie slot's multi-value ER resolved to {Count} distinct library movies ({Names}); asking which to play (JF-877)",
                erMovies.Count,
                string.Join(", ", erMovies.Select(v => v.Name)));
            var matches = erMovies
                .Take(DisambiguationHelper.FirstMatchStateCap)
                .Select(v => (v.Id, v.Name, (string?)Launch.GetImageUrl(v.Id.ToString("N"), user)))
                .ToList();
            return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeVideo, locale, context);
        }

        IReadOnlyList<BaseItem> videos;
        if (erMovies != null && erMovies.Count == 1)
        {
            // The gate's collapse survivor IS the exact-name evidence the search
            // chain approximates, so every tier below (raw query, stripped
            // retry, fuzzy) is stood down on this leg.
            Logger.LogInformation(
                "PlayMovie: the movie slot's multi-value ER collapsed to the single library movie '{Movie}'; playing it (JF-877)",
                erMovies[0].Name);
            videos = erMovies;
        }
        else
        {
            var videoSearchQuery = new InternalItemsQuery()
            {
                User = jellyfinUser,
                Recursive = true,
                SearchTerm = titleQuery,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(videoSearchQuery, user, _libraryManager);

            videos = await RetryAsync(
                () => _libraryManager.GetItemList(videoSearchQuery),
                "GetVideos",
                cancellationToken).ConfigureAwait(false);
        }

        if (videos.Count == 0)
        {
            // JF-509 raw-first retry, BEFORE the fuzzy fallback: the fill may have
            // swallowed the carrier noun ('film ada' for 'ada'), and the stripped
            // exact query is a far higher-probability hit than a fuzzy near-miss on
            // the raw value (live: the fuzzy answered 'Intendevi <unrelated>?' while
            // 'ada' existed). A title genuinely starting with the noun was already
            // found by the raw query above.
            string? strippedTitle = StripLeadingMediaNoun(titleQuery);
            if (strippedTitle != null)
            {
                Logger.LogDebug("PlayVideo: raw title miss, retrying stripped '{Stripped}' (JF-509)", strippedTitle);
                var strippedQuery = new InternalItemsQuery
                {
                    User = jellyfinUser,
                    Recursive = true,
                    SearchTerm = strippedTitle,
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                    DtoOptions = new DtoOptions(true)
                };
                ApplyLibraryFilter(strippedQuery, user, _libraryManager);
                videos = await RetryAsync(
                    () => _libraryManager.GetItemList(strippedQuery),
                    "GetVideosStrippedTitle",
                    cancellationToken).ConfigureAwait(false);
            }

            if (videos.Count == 0)
            {
                // When a stripped title exists the fuzzy runs on the STRIPPED value:
                // the swallowed-carrier shape ('film ada') fuzzies against 'ada', the
                // title the user meant, not against the raw 'film ada' (live 19:38:
                // raw-fuzzy matched 'Cicada' score 50 while 'Ada: My Mother the
                // Architect' existed and fuzzy-on-'ada' is the high-confidence hit).
                string fuzzyQuery = strippedTitle ?? titleQuery;
                Logger.LogDebug("PlayVideo: fuzzy fallback on '{FuzzyQuery}' (JF-509)", fuzzyQuery);
                var fuzzy = await Search.SearchItemsFuzzyAsync(fuzzyQuery, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.Movie, BaseItemKind.Episode }, cancellationToken, "PlayVideoFuzzyFallback", locale: locale).ConfigureAwait(false);
                if (fuzzy != null)
                {
                    videos = new List<BaseItem> { fuzzy.Value.Item };
                }
                else
                {
                    // JF-882 (review F6): a FREE-TEXT title (PlayVideoIntent
                    // only; the PlayMovie branch answered this question at the
                    // entry guard) that reaches the honest not-found while
                    // carrying a LEADING song-carrier phrase is the misroute
                    // shape: a song ask NLU-steered here runs the video search
                    // on the unstripped fill and would answer NotFoundVideo.
                    // Delegate to the PlaySong handler instead (the n-gram
                    // search and the JF-871 cascade run on the song path; its
                    // video arm re-searches the carrier-stripped title, so a
                    // carrier-leading movie title the raw query missed keeps a
                    // rescue path). Placement is RAW-FIRST on purpose, the
                    // JF-509 discipline this slot already follows: every
                    // genuine-movie avenue (raw query, media-noun-stripped
                    // retry, fuzzy) runs BEFORE the delegation, because the
                    // whole-span escape is unavailable on a slot with no ER
                    // canonical and a movie genuinely titled "The Song of
                    // Bernadette" must stay findable by its raw title here.
                    // Accepted cost (the JF-624 tap-delegation family shape):
                    // the misrouted ask hears the searching announcement twice
                    // and runs the video chain plus the song chain, each tier
                    // individually budgeted.
                    if (MultiValueErDisambiguation.IsSongCarrierMisroute(rawTitleFill, canonicalValue: null, out string missTailSongQuery))
                    {
                        Logger.LogDebug("PlayVideo: video miss on a song-carrier title fill, delegating to PlaySong with '{SongQuery}'", missTailSongQuery);
                        return await _playSongHandler.HandleAsync(
                            MultiValueErDisambiguation.BuildSongDelegation(intentRequest, rawTitleFill!),
                            context, user, session, cancellationToken).ConfigureAwait(false);
                    }

                    return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundVideo", locale, titleQuery));
                }
            }
        }

        if (videos.Count > 1)
        {
            BaseItem? videoMatch = null;
            // JF-509: the multi-result FuzzyMatch runs on the STRIPPED title when one
            // exists (same rationale as the fuzzy below: the raw 'film ada' scores the
            // unrelated 'Cicada' at 50 while the stripped 'ada' picks the real match
            // among candidates the stripped query returned).
            string? multiMatchQuery = StripLeadingMediaNoun(titleQuery) ?? titleQuery;
            // JF-776: scoring through the romaji reading (ScoringName), speech
            // keeps the display name (the JF-755 speechSelector seam).
            var (missOutcome, missResponse) = await HandleFuzzyMiss(
                multiMatchQuery,
                videos,
                v => Util.KeywordMatcher.ScoringName(v.Name),
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeVideo,
                locale,
                best =>
                {
                    videoMatch = best;
                    return Task.FromResult<SkillResponse>(null!);
                },
                user: user,
                speechSelector: v => v.Name).ConfigureAwait(false);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                if (missResponse != null)
                {
                    return missResponse;
                }

                videos = new List<BaseItem> { videoMatch! };
            }
            else
            {
                var matches = videos.Take(DisambiguationHelper.FirstMatchStateCap).Select(v => (v.Id, v.Name, (string?)Launch.GetImageUrl(v.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeVideo, locale, context);
            }
        }

        BaseItem video = videos[0];

        // Check for existing playback progress and announce resume position.
        // Note: Alexa VideoApp does not support seek/offset natively, so the video
        // will start from the beginning, but we inform the user where they left off.
        UserItemData? userData = _userDataManager.GetUserData(jellyfinUser!, video);
        long resumeTicks = userData?.PlaybackPositionTicks ?? 0;
        if (resumeTicks > 0)
        {
            Logger.LogInformation("PlayVideo: resuming {Title} from {Position}", video.Name, ResumeMath.FormatPosition(resumeTicks));
        }

        // Alexa VideoApp does not support seek/offset natively (the video starts from the
        // beginning); the announce only informs the user where they left off.
        // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
        // JF-501: the announce is spoken progressively (directive-only final response).
        // JF-587: the episode screenless degrade (audio-only on Dots); movies keep
        // the capability refusal inside the builder.
        // JF-699 item 5: the session writes follow the launch build (the ordering
        // policy lives on EnsureStreamTokenDeliverable; the codec-routed remux URL
        // is token-gated UNCONDITIONALLY on this path, not only in seek mode).
        SkillResponse response = await Launch.BuildEpisodeLaunchResponseAsync(
            context,
            request,
            locale,
            video,
            user,
            Launch.GetVideoAppLaunchUrl(video, user),
            resumeTicks,
            Launch.BuildVideoLaunchSpeech(video, locale, resumeTicks, Launch.GetAnnounceNowPlaying(user))).ConfigureAwait(false);

        // JF-714/JF-718: the now-playing writes ride a DELIVERED launch; the
        // rationale lives on AttachNowPlayingIfLaunched.
        PlaybackLaunchBuilder.AttachNowPlayingIfLaunched(response, session, video);
        return response;
    }

    /// <summary>
    /// Media nouns (+ definite/indefinite articles) the movie-title fill can swallow,
    /// per language family of the 17 locales (JF-509). The strip is prefix-only and
    /// repeated (fill can include 'il film', 'film', 'un film', 'la película', ...).
    /// Every entry carries its trailing space implicitly via the match below, so
    /// word-fragment titles ('filmstar') never match.
    /// </summary>
    private static readonly string[] LeadingMediaNounPrefixes =
    {
        // it
        "il film", "un film", "il movie", "un movie", "film", "movie",
        // it/en video carriers (review finding: the 2026-23 title-less samples added
        // «Riproduci un video»/«riprodurre un video {title}», whose swallowed fill
        // ('un video ada') had no rescue entry)
        "il video", "un video", "the video", "a video", "video",
        // en
        "the movie", "a movie", "movie",
        // de
        "der film", "den film", "ein film", "film",
        // es
        "la película", "una película", "película", "la pelicula", "pelicula",
        // fr
        "le film", "un film",
        // pt
        "o filme", "um filme", "filme",
        // nl
        "de film", "een film",
        // hi / ar / ja are handled by their own carriers being non-latinate; their
        // models carry the noun inside the sample carrier so the fill does not split it
    };

    /// <summary>
    /// Strips a leading localized media noun (with articles) that the statistical
    /// fill swallowed into the title (JF-509; the SearchQuery title has no catalog
    /// to anchor its boundary). Language-agnostic like StripSongCarrierPhrase.
    /// Returns null when the value does not change or is only the noun/article:
    /// the raw-first caller retries only on a real change.
    /// </summary>
    /// <param name="title">The raw title slot value.</param>
    /// <returns>The cleaned title, or null when there is nothing new to retry.</returns>
    private static string? StripLeadingMediaNoun(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string cleaned = title.Trim();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string prefix in LeadingMediaNounPrefixes)
            {
                bool withSpace = cleaned.Length > prefix.Length
                    && cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && char.IsWhiteSpace(cleaned[prefix.Length]);
                if (withSpace)
                {
                    cleaned = cleaned[prefix.Length..].TrimStart();
                    changed = true;
                    break;
                }

                // The whole value is the noun/article: nothing searchable remains.
                if (string.Equals(cleaned, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
        }

        // Unchanged input means there is nothing new to retry (the caller's raw query
        // already ran): null, never the original value.
        return string.Equals(cleaned, title.Trim(), StringComparison.Ordinal) ? null : cleaned;
    }
}

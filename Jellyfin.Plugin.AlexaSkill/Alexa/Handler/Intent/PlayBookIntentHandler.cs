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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayBookIntent — searches for audiobooks and plays their audio content.
/// Resumes from the last position when the user has existing progress.
/// </summary>
public class PlayBookIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager _queueManager;

    // JF-807: the Layer-1 warming gate's index stand-in (null in test/minimal
    // setups: no gate; the JF-806 YesIntent book-confirm shape).
    private readonly IArtistIndex? _artistIndex;

    // JF-882: the song-carrier guard's delegation target, REQUIRED (the
    // JF-876/JF-624 family shape: a dropped DI registration fails at container
    // construction, not on the first misrouted request).
    private readonly PlaySongIntentHandler _playSongHandler;

    public PlayBookIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        PlaySongIntentHandler playSongHandler,
        DeviceQueueManager queueManager,
        IArtistIndex? artistIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _playSongHandler = playSongHandler;
        _queueManager = queueManager;
        _artistIndex = artistIndex;
    }

    /// <summary>
    /// Map the audiobook search candidates onto BOOK granularity (JF-793 Finding 3):
    /// each candidate through the audiobook climb (AudiobookItems.TryResolveBookFolder),
    /// deduped by resolved id, preserving first-occurrence order. Chapter leaves
    /// collapse onto their book folder, so the multi-match disambiguation presents
    /// ONE entry per book (named for the book, whose confirm payload is the folder
    /// id, and whose name clears the auto-play bar the "- Chapter N" tails dragged
    /// below) instead of N chapter-granular choices. Single-file books, failed
    /// climbs, and the shared-container rejection (the collapsed book under a
    /// container) keep their own entry: they ARE distinct books. The replaced list
    /// is returned even when no dedup collapsed anything (code-review F2: distinct
    /// books' leaves must also present as folders, not only same-book leaves). The
    /// post-disambiguation climb in <see cref="HandleAsync"/> stays as the tolerant
    /// safety net (on a Folder the helper harmlessly returns null).
    /// </summary>
    private IReadOnlyList<BaseItem> NormalizeBookCandidates(IReadOnlyList<BaseItem> candidates)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        // First-occurrence order BY CONSTRUCTION (gate-marker F4): a dictionary's
        // Values enumeration is an implementation detail, so the dedup walks the
        // source list with a seen-set instead and the doc's order promise is the
        // code's actual guarantee.
        // JF-797 item 4: the batched climb. The per-ask scratchpad resolves each
        // DISTINCT ParentId once (9 chapter leaves of 3 books cost 3 GetItemById
        // calls, not 9), and the per-candidate verdicts stay exact (the AudioBook
        // gate, the empty-ParentId shape, and the path check run per candidate;
        // see the overload's doc).
        List<BaseItem> byBook = new();
        HashSet<Guid> seen = new();
        Dictionary<Guid, Folder?> parentFolders = new();
        foreach (BaseItem candidate in candidates)
        {
            BaseItem book = AudiobookItems.TryResolveBookFolder(candidate, _libraryManager, parentFolders) ?? candidate;
            if (seen.Add(book.Id))
            {
                byBook.Add(book);
            }
        }

        return byBook;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(
            intentRequest.Intent.Name, IntentNames.PlayBook, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(
        Request request,
        Context context,
        Entities.User user,
        SessionInfo session,
        CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        var disabled = IfFeatureDisabled(c => c.BooksEnabled, request);
        if (disabled != null)
        {
            return disabled;
        }

        IntentRequest intentRequest = (IntentRequest)request;

        string? book = intentRequest.Intent.Slots?.TryGetValue("book", out var bookSlot) == true
            ? bookSlot.Value
            : null;

        // JF-882: the RAW fill (pre-romanization), captured for the
        // song-carrier misroute guard at the resolution miss below.
        string? rawBookFill = book;

        if (string.IsNullOrWhiteSpace(book))
        {
            return ResponseBuilder.Ask(
                ResponseStrings.Get("ElicitBookName", locale),
                new Reprompt(ResponseStrings.Get("ElicitBookName", locale)));
        }

        // JF-807 Layer-1 gate: books have no in-memory index of their own, so
        // this is the coarse artist-index stand-in for the shared cold database
        // (the shared rule lives on IndexWarmingGate; the PlayAlbum precedent),
        // matching the JF-806 book-confirm gate so the ask and its confirm
        // answer identically in the warming window. Placement: AFTER the books
        // gate (the confirm's own order: the warming+disabled intersection
        // answers FeatureDisabled) and AFTER the empty-slot elicit. THE REAL
        // DISCRIMINATOR for elicit-then-gate vs gate-then-elicit is WHERE THE
        // HANDLER'S FLAG GATE SITS (gate-marker tail F2): PlaySong/PlayAlbum's
        // music gate sits AFTER their elicit, forcing their warming gate above
        // it; QueryArtistLibrary and AddToQueue are Dialog.ElicitSlot flows
        // that still order elicit-then-gate because their flag gates PRECEDE
        // the elicit. Never generalize from the elicit's mechanism - only from
        // the flag gate's position. BEFORE the "searching" announcement.
        GuardIndexReady(_artistIndex);

        // JF-643: the book title feeds the SearchTerm query and the fuzzy cascade
        // below, both against Latin library names; romanize the query once.
        book = Util.KatakanaRomanizer.Romanize(book);

        RunFireAndForget(SendProgressiveResponse(
            context, request, ResponseStrings.Get("SearchingBook", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // JF-862: the shared named-item resolve chain (the SearchTerm tier, the
        // zero-result fuzzy fallback) with THIS site's multi-match policy:
        // CallerArbitrates, because the arbitration is the Handler-layer prompt
        // flow below (NormalizeBookCandidates + HandleFuzzyMiss + AskFirstMatch),
        // which the Util-layer chain cannot host.
        NamedItemResolution resolution = await Search.ResolveNamedItemAsync(
            book,
            jellyfinUser,
            user,
            _libraryManager,
            new[] { BaseItemKind.AudioBook },
            NamedItemMultiMatchPolicy.CallerArbitrates,
            "GetAudiobooks",
            "PlayBookFuzzyFallback",
            locale,
            cancellationToken).ConfigureAwait(false);

        if (resolution.Item is null && resolution.Matches is null)
        {
            // JF-882: the JF-876 song-carrier misroute guard, at the MISS TAIL
            // (code-review round 2): the book slot is the SAME catalog-backed
            // shape the movie slot is (AudiobookTitle is catalog-synced in
            // every locale since JF-823), so the same ER pull can select
            // PlayBookIntent for a SONG carrier ask ("metti il brano X", X
            // also an audiobook title) on a future trainer roll, and the
            // statistical fill swallows the carrier noun (the JF-509 class).
            // Unlike the movie site (whose preemptive JF-876 shape is safe
            // because a carrier-titled movie stays reachable through
            // PlayVideoIntent's free-text title), NO other intent plays an
            // audiobook and the song path's JF-871 cascade has no book tier,
            // so a preemptive delegation would make a genuine carrier-titled
            // book («La Canzone di Natale» on a stale catalog) permanently
            // unplayable. The raw-first tail fixes both sides: the book search
            // above already played every findable title, and only the
            // confirmed miss with a leading carrier in the fill delegates to
            // PlaySong (whole-span escape inside: a canonical-verbatim fill is
            // a VERIFIED book ask and keeps the honest book not-found).
            // Accepted cost (the JF-624 tap-delegation family shape): the
            // misrouted ask pays the book search first, hears the searching
            // announcement twice, and the disabled-books flag answers before
            // the tail can delegate.
            if (MultiValueErDisambiguation.IsSongCarrierMisroute(
                    rawBookFill,
                    Util.SlotValueHelper.GetCanonicalValue(intentRequest, "book"),
                    out string detectedSongQuery))
            {
                Logger.LogDebug("PlayBook: book miss on a song-carrier fill, delegating to PlaySong with '{SongQuery}'", detectedSongQuery);
                return await _playSongHandler.HandleAsync(
                    MultiValueErDisambiguation.BuildSongDelegation(intentRequest, rawBookFill!),
                    context, user, session, cancellationToken).ConfigureAwait(false);
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundBook", locale, book));
        }

        // JF-793 Finding 3: BOOK-granular candidates before the disambiguation
        // consumers (the illusory chapter-granular choice, and the "- Chapter N"
        // name tails dragging the fuzzy scores below the auto-play bar). The shape
        // rationale and the pass-through list live on NormalizeBookCandidates; the
        // single-match and fuzzy-hit legs skip it (its Count <= 1 short-circuit
        // made them pass-throughs pre-JF-862 too, and resolution.Item is non-null
        // there by the one-payload invariant).
        IReadOnlyList<BaseItem> books = resolution.Matches is { } rawMatches
            ? NormalizeBookCandidates(rawMatches)
            : new List<BaseItem> { resolution.Item! };

        if (books.Count > 1)
        {
            BaseItem? bookMatch = null;
            // JF-776: scoring through the romaji reading (ScoringName), speech
            // keeps the display name (the JF-755 speechSelector seam).
            var (missOutcome, missResponse) = await HandleFuzzyMiss(
                book,
                books,
                b => Util.KeywordMatcher.ScoringName(b.Name),
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeAlbum,
                locale,
                best =>
                {
                    bookMatch = best;
                    return Task.FromResult<SkillResponse>(null!);
                },
                user: user,
                speechSelector: b => b.Name).ConfigureAwait(false);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                if (missResponse != null)
                {
                    return missResponse;
                }

                books = new List<BaseItem> { bookMatch! };
            }
            else
            {
                var matches = books.Take(3).Select(b => (b.Id, b.Name, (string?)Launch.GetImageUrl(b.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(
                    matches, DisambiguationHelper.MediaTypeAlbum, locale, context);
            }
        }

        // JF-795: everything from the chapter-leaf climb down (the initial page,
        // the JF-361 single-file fallback, the resume decision with the JF-793
        // finding-4 deep-resume re-slice, the JF-693/JF-699 refusal-before-state
        // ordering, the JF-674 continuation mint, and the launch branches) lives
        // on the ONE shared resolved-book play flow, so the disambiguation
        // confirm rides exactly what the direct ask plays (the
        // PodcastEpisodeResolver precedent). The spoken name for the no-content
        // Tell keeps the raw slot value the head has always spoken.
        return await AudiobookPlayResolver.PlayBookAsync(
            _libraryManager, Launch, Logger, "PlayBook",
            books[0], book, jellyfinUser!, user, session, context, request, locale,
            _userDataManager, _queueManager, cancellationToken).ConfigureAwait(false);
    }
}

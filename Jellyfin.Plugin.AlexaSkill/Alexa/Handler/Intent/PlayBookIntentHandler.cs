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
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
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

    public PlayBookIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager queueManager,
        IArtistIndex? artistIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _queueManager = queueManager;
        _artistIndex = artistIndex;
    }

    /// <summary>
    /// Map the audiobook search candidates onto BOOK granularity (JF-793 Finding 3):
    /// each candidate through <see cref="AudiobookItems.TryResolveBookFolder"/>,
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
        List<BaseItem> byBook = new();
        HashSet<Guid> seen = new();
        foreach (BaseItem candidate in candidates)
        {
            BaseItem book = AudiobookItems.TryResolveBookFolder(candidate, _libraryManager) ?? candidate;
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
        // answers FeatureDisabled) and AFTER the empty-slot elicit (a plain Ask
        // with no Dialog.ElicitSlot flow; the QueryArtistLibrary/AddToQueue
        // shape, not the PlaySong/PlayAlbum warming-before-elicit exception),
        // BEFORE the "searching" announcement.
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

        var bookQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = book,
            IncludeItemTypes = new[] { BaseItemKind.AudioBook },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(bookQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> books = await RetryAsync(
            () => _libraryManager.GetItemList(bookQuery),
            "GetAudiobooks",
            cancellationToken).ConfigureAwait(false);

        if (books.Count == 0)
        {
            var fuzzy = await Search.SearchItemsFuzzyAsync(book, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.AudioBook }, cancellationToken, "PlayBookFuzzyFallback", locale: locale).ConfigureAwait(false);
            if (fuzzy != null)
            {
                books = new List<BaseItem> { fuzzy.Value.Item };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundBook", locale, book));
            }
        }

        // JF-793 Finding 3: BOOK-granular candidates before the disambiguation
        // consumers (the illusory chapter-granular choice, and the "- Chapter N"
        // name tails dragging the fuzzy scores below the auto-play bar). The shape
        // rationale and the pass-through list live on NormalizeBookCandidates.
        books = NormalizeBookCandidates(books);

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

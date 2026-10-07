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
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE resolved-book play flow (JF-795, the PodcastEpisodeResolver precedent):
/// the shared resolve-to-launch tail every entry point that plays a MATCHED or
/// CONFIRMED audiobook must ride, extracted from PlayBookIntentHandler's own
/// HandleAsync so the direct ask and the disambiguation "yes" cannot drift. The
/// flow owns, in order: the JF-791 chapter-leaf climb to the book folder; the
/// initial chapters page through the ONE shared page query (the JF-673
/// unknown-total regime); the JF-361 single-file fallback; the JF-790
/// untagged/tie detection and filename-order fallback (the full book fetched
/// once, sorted with the ONE shared trailing-filename comparator, the
/// continuation paging the sorted list in memory); the resume decision
/// (<see cref="ResumeMath.FindResumeTrackIndex(IReadOnlyList{BaseItem}, Jellyfin.Database.Implementations.Entities.User, IUserDataManager, DeviceQueueManager, string, bool, ILogger)"/>) plus the JF-793 finding-4
/// deep-resume re-slice when progress sits beyond the page (MOOT in the
/// JF-790 shape, whose full-book scan subsumes it); the
/// JF-693/JF-699 refusal-before-state ordering (queue, device queue, and
/// progressive continuation are written only after the launch verdict); the
/// JF-674 MintedQueueItemIds binding; and the launch branches (NativeControlsForBooks
/// tracked/cold/fresh VideoApp, flat AudioPlayer chapter resume). Callers own
/// everything BEFORE the match: the slot read, the search, the fuzzy cascade,
/// the disambiguation prompt, and the feature gate.
/// ROUTING DECISION (the task's preferred branch, evaluated and taken): the
/// confirm context reaches this composition cleanly through this static Util
/// shape taking the caller's collaborators as parameters, exactly the way
/// YesIntentHandler already consumes PodcastEpisodeResolver for podcast
/// confirms; the one dependency the confirm leg lacked (IUserDataManager, the
/// resume axis) is now ctor-injected there, so no caller-local replication of
/// the page+resume+mint flow exists anywhere.
/// </summary>
public static class AudiobookPlayResolver
{
    /// <summary>
    /// Plays a resolved audiobook: climbs a chapter leaf to its book folder,
    /// pages the chapters, resolves the resume position (deep progress
    /// included), installs the queue/device-queue/continuation state after the
    /// launch verdict, and returns the launch response (VideoApp concat under
    /// NativeControlsForBooks, flat AudioPlayer otherwise), or a localized Tell
    /// when the book has no playable audio.
    /// </summary>
    /// <param name="libraryManager">The library manager for the chapters queries and the folder climb.</param>
    /// <param name="launch">The caller's launch builder (stream URLs and the launch response chokepoints).</param>
    /// <param name="logger">The caller's logger (page/resume triage lines).</param>
    /// <param name="logLabel">The caller-scoped log prefix ("PlayBook" for the direct ask, "Yes" for the confirm).</param>
    /// <param name="book">The matched or confirmed book item: a book folder, a chapter leaf (climbed here), or a single-file AudioBook.</param>
    /// <param name="spokenBookName">The name to speak in the no-content Tell: the raw spoken slot on the direct ask. Null (the confirm's shape) speaks the resolved book's own name AFTER the climb, the pre-JF-795 confirm leg's speech.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the queries and the resume data.</param>
    /// <param name="user">The plugin user (stream URL token, announce toggles).</param>
    /// <param name="session">The Jellyfin session receiving the queue (device id, now-playing writes).</param>
    /// <param name="context">The Alexa context (device id for the device queue and the continuation store).</param>
    /// <param name="request">The skill request (the progressive-announce vehicle on the VideoApp arm).</param>
    /// <param name="locale">The request locale for response strings.</param>
    /// <param name="userDataManager">User data manager for the resume-track lookup.</param>
    /// <param name="queueManager">Optional per-device queue manager (crash recovery; null skips the ItemPositionState tier and the device-queue write).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The launch response, or the localized no-content Tell.</returns>
    public static async Task<SkillResponse> PlayBookAsync(
        ILibraryManager libraryManager,
        PlaybackLaunchBuilder launch,
        ILogger logger,
        string logLabel,
        BaseItem book,
        string? spokenBookName,
        JellyfinUser jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        Request request,
        string locale,
        IUserDataManager userDataManager,
        DeviceQueueManager? queueManager,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jellyfinUser);

        // JF-791: the match above for a multi-chapter book can be a CHAPTER leaf (the
        // shape rationale and the null contract live on
        // AudiobookItems.TryResolveBookFolder); re-point it at the book folder so
        // every head-query/continuation/name read below runs on the BOOK. Null keeps
        // the match itself: its own track (the JF-361 duality in the zero-children
        // branch below).
        if (AudiobookItems.TryResolveBookFolder(book, libraryManager) is { } bookFolder)
        {
            logger.LogDebug(
                "{Label}: book item '{MatchName}' is an audiobook chapter leaf, climbing to book folder '{BookName}' ({BookId})",
                logLabel, book.Name, bookFolder.Name, bookFolder.Id);
            book = bookFolder;
        }

        // Initial page through the ONE shared chapters-page query's scoped sibling
        // (JF-670; JF-767 joined the scope axis so head, confirm, and tail all run
        // the same JF-666 pairing and cannot drift apart): the continuation fetcher
        // and the YesIntent PlayBook confirm build the same shape.
        // JF-673: unknownTotalOnFallback because this page drives a pagination loop.
        // On NRE-class servers the page arrives through the GetItemList fallback,
        // whose honest total is UNKNOWN: a page-size total would read as "complete"
        // here, the continuation store below would never engage, and the book would
        // truncate at this page exactly on the servers the NRE guard exists for.
        QueryResult<BaseItem> bookTracks = await RetryHelper.ExecuteWithRequestBudgetAsync(
            () => SearchService.SafeGetItemsResult(libraryManager,
                QueueContinuationFetcher.BuildScopedAudiobookChaptersQuery(
                    jellyfinUser, user, libraryManager, logger, book.Id,
                    startIndex: 0, limit: ProgressiveQueueConstants.GetInitialFetchSize()),
                logger,
                unknownTotalOnFallback: true),
            logger,
            "GetBookTracks",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Single-file audiobooks: the AudioBook item IS the audio track itself.
        // Multi-file audiobooks: children tracks exist under a parent folder.
        // JF-673: the zero check is regime-aware (the ONE predicate shared with the
        // album head, QueueContinuationFetcher.PageHasNoItems).
        IReadOnlyList<BaseItem> trackItems;
        if (QueueContinuationFetcher.PageHasNoItems(bookTracks))
        {
            if (book.MediaType == MediaType.Audio)
            {
                trackItems = new List<BaseItem> { book };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NoContentInBook", locale, spokenBookName ?? book.Name));
            }
        }
        else
        {
            trackItems = bookTracks.Items;
        }

        // JF-790: the untagged/tie detection (the probe's two shapes, evidence in
        // the JF-672/JF-790 task records). When the page proves the DB order
        // cannot be trusted as chapter order (every row lacks IndexNumber, the
        // untagged class whose order rests on lexicographic SortName alone; or
        // two rows share the full (SortName, Name) key, server whim), the queue
        // falls back to the ONE datum that orders these books: the trailing
        // filename number, through the ONE shared comparator the concat endpoint
        // already uses (ChapterFileNameOrder; the endpoint's former private copy
        // consumed the same helper since this change, so the seek-mode resume
        // math over the concat timeline and this queue describe the same book).
        // The whole book is fetched ONCE, unpaged, exactly the shape the
        // deep-resume scan below uses (the composition continues at the resume
        // fork); when the page already carried the whole book (no more rows
        // remain), the page rows themselves are the full list and no second
        // query runs. The TAGGED class never enters this branch (its rows carry
        // IndexNumber and distinct keys): byte-identical DB paging, per the
        // task's hard requirement.
        List<BaseItem>? fileNameOrderedBook = null;
        if (Util.ChapterFileNameOrder.PageDistrustsDbOrder(trackItems))
        {
            if (QueueContinuationFetcher.InitialPageHasMore(bookTracks))
            {
                // The page carried rows but more remain: the whole book is
                // fetched ONCE, unpaged, through the ONE scoped-unpaged fetch the
                // deep-resume block shares (FetchUnpagedBookAsync below).
                QueryResult<BaseItem> fullFetch = await FetchUnpagedBookAsync("GetBookTracksFileNameOrder").ConfigureAwait(false);
                if (fullFetch.Items.Count == 0)
                {
                    // The page served rows but the full fetch answered empty (the
                    // library emptied between the two queries): keep the page's DB
                    // answer rather than launching an empty queue.
                    logger.LogWarning(
                        "{Label}: JF-790 filename-order fetch for '{BookName}' returned no rows; keeping the DB page order",
                        logLabel, book.Name);
                }
                else
                {
                    fileNameOrderedBook = Util.ChapterFileNameOrder.SortByTrailingFileNameNumber(fullFetch.Items);
                }
            }
            else
            {
                // The page already carried the whole book (no more rows remain):
                // the page rows themselves are the full list, no second query.
                fileNameOrderedBook = Util.ChapterFileNameOrder.SortByTrailingFileNameNumber(trackItems);
            }

            if (fileNameOrderedBook != null)
            {
                trackItems = fileNameOrderedBook
                    .Take(ProgressiveQueueConstants.GetInitialFetchSize())
                    .ToList();

                logger.LogInformation(
                    "{Label}: JF-790 untagged/tie shape detected for '{BookName}' ({BookId}); serving {ChapterCount} chapters in filename order (first '{FirstName}')",
                    logLabel, book.Name, book.Id, fileNameOrderedBook.Count, trackItems[0].Name);
            }
        }

        // Check for existing progress; resume from the last position. (The moved
        // debug template de-dashes the handler original: log wording only.)
        logger.LogDebug(
            "{Label}: checking resume for '{BookName}' ({BookId}) with {TrackCount} tracks",
            logLabel, book.Name, book.Id, trackItems.Count);

        for (int debugIdx = 0; debugIdx < trackItems.Count; debugIdx++)
        {
            UserItemData? dbgData = userDataManager.GetUserData(jellyfinUser, trackItems[debugIdx]);
            logger.LogDebug(
                "{Label} track[{Idx}]: '{TrackName}', Played={Played}, PositionTicks={Ticks}",
                logLabel, debugIdx, trackItems[debugIdx].Name,
                dbgData?.Played, dbgData?.PlaybackPositionTicks);
        }

        int startIndex;
        long resumeTicks;
        int continuationStartIndex;
        int continuationTotalCount;
        bool continuationHasMore;

        // The ONE unpaged full-book fetch (the /simplify hoist): the JF-790
        // filename-order branch and the JF-793 deep-resume block below run the
        // same scoped query; only the triage label differs, so a future scoping
        // change (the JF-812 decision may touch exactly this query) cannot be
        // applied to one arm and missed on the other.
        async Task<QueryResult<BaseItem>> FetchUnpagedBookAsync(string label)
            => await RetryHelper.ExecuteWithRequestBudgetAsync(
                () => SearchService.SafeGetItemsResult(libraryManager,
                    QueueContinuationFetcher.BuildScopedAudiobookChaptersQueryUnpaged(
                        jellyfinUser, user, libraryManager, logger, book.Id),
                    logger),
                logger,
                label,
                cancellationToken: cancellationToken).ConfigureAwait(false);

        // The ONE continuation rebase onto a fetch-all list (the /simplify hoist;
        // the fetch-all list is itself the honest total in both regimes, known-
        // total pages and the JF-673 end-unknown fallback alike), and the ONE
        // re-page at a resume chapter found in it. Both the JF-790 fork and the
        // JF-793 deep block below re-page through these; the "verbatim" contract
        // between the two shapes is now structural, not by comment.
        void RebaseContinuationOnFullBook(int fullBookCount, int resumeIndex)
        {
            // StartIndex counts the chapters the queue has consumed (page start +
            // page count); the maybe-more bar is the ONE named decision
            // (QueueContinuationFetcher.InitialPageHasMore(int, int)).
            continuationStartIndex = resumeIndex + trackItems.Count;
            continuationTotalCount = fullBookCount;
            continuationHasMore = QueueContinuationFetcher.InitialPageHasMore(
                continuationStartIndex, continuationTotalCount);
        }

        void RePageFullBookAt(IReadOnlyList<BaseItem> fullBook, int resumeIndex)
        {
            // The re-sliced page starts exactly at the position-holding chapter,
            // so the page-relative answer is (0, resumeTicks) by construction in
            // every return shape of FindResumeTrackIndex (in-progress hit, cached
            // position, after-last-played): no re-scan of the slice.
            trackItems = fullBook
                .Skip(resumeIndex)
                .Take(ProgressiveQueueConstants.GetInitialFetchSize())
                .ToList();
            startIndex = 0;
            RebaseContinuationOnFullBook(fullBook.Count, resumeIndex);

            logger.LogInformation(
                "{Label}: resume scan found chapter {ChapterIndex} ('{TrackName}') in the full book; re-paging the book at it",
                logLabel, resumeIndex, fullBook[resumeIndex].Name);
        }

        // JF-790 composition with the JF-793/JF-797 deep-resume machinery: the
        // sorted full book IS the unpaged list the deep scan wants, so the ONE
        // resume decision runs over it directly. No page-bounded resume exists
        // in this shape (the page was replaced the moment the book was in hand),
        // and the deep-resume gate below stays MOOT: its whole purpose is
        // deciding whether to pay the unpaged full-book fetch, which the ORDER
        // fix already paid. Any positive resume chapter re-pages the book at the
        // position-holding chapter (the deep block's re-page, ONE definition
        // above), so the launch, the queue, and the continuation all start
        // there; the continuation pages the SORTED list in memory (CachedTracks),
        // never the DB order again.
        IReadOnlyList<BaseItem> resumeScan = fileNameOrderedBook ?? trackItems;
        (startIndex, resumeTicks) = ResumeMath.FindResumeTrackIndex(
            resumeScan, jellyfinUser, userDataManager, queueManager, session.DeviceId, resumePosition: true, logger);

        logger.LogInformation(
            "{Label}: FindResumeTrackIndex returned startIndex={StartIndex}, resumeTicks={Ticks} for '{BookName}'",
            logLabel, startIndex, resumeTicks, book.Name);

        if (fileNameOrderedBook != null)
        {
            int resumeChapterIndex = startIndex;
            if (resumeChapterIndex > 0)
            {
                RePageFullBookAt(fileNameOrderedBook, resumeChapterIndex);
            }
            else
            {
                RebaseContinuationOnFullBook(fileNameOrderedBook.Count, resumeChapterIndex);
            }
        }
        else
        {
            // The continuation bookkeeping, computed here so the deep-resume block below
            // can rebase it: how many chapters the queue has consumed (page start +
            // page count; the database offset is independent of the resume slice) and
            // whether more remain. JF-673: the gate is regime-aware (the ONE decision
            // shared with the album head, see QueueContinuationFetcher.InitialPageHasMore).
            continuationStartIndex = bookTracks.Items.Count;
            continuationTotalCount = bookTracks.TotalRecordCount;
            continuationHasMore = QueueContinuationFetcher.InitialPageHasMore(bookTracks);
        }

        // JF-793 Finding 4: the page-1-bounded resume. FindResumeTrackIndex scanned
        // only the initial page, so UserData progress on a chapter BEYOND the page
        // (chapter 22 of 26) was invisible and the fresh ask relaunched from chapter
        // 1 at 0:00 (pre-JF-791 the same ask played the matched chapter at its
        // position, then silence; the album precedent is not liftable, JF-625
        // criterion 3 is the video-route tracker override). The bounded resolution:
        // when the page yields no position AND the book extends beyond the page,
        // fetch the book once unpaged (the concat endpoint's fetch-all shape,
        // JF-784) and re-run the ONE resume decision on the full chapter list; a
        // position found beyond the page re-slices the page at that chapter, so the
        // launch, the queue, and the continuation all start at the
        // position-holding chapter.
        // JF-797 item 1 (the masking shape): the gate no longer requires
        // startIndex == 0. A PLAYED PREFIX on page 1 makes FindResumeTrackIndex
        // answer (after-last-played > 0, ticks 0), which the old gate read as
        // "resolved": the deep scan stayed cold even when a deeper in-progress
        // chapter existed beyond the page, and resume landed at the shallow prefix
        // position. ticks == 0 is the honest "the page's evidence is incomplete"
        // signal in every page answer (fresh page, all-played page, played
        // prefix); a ticks > 0 answer IS the first in-progress chapter of the
        // whole book (the scan returns on the first hit and the page is a list
        // prefix), so no deep fetch can improve it.
        // JF-797 item 2 (the fresh-ask discriminator): the unpaged fetch is gated
        // by QueueContinuationFetcher.MayHaveResumeRelevantUserDataAsync (two
        // bounded IsPlayed/IsResumable probe queries over the same scoped page
        // shape; a miss proves no row carries resume-relevant user data, so a
        // first-ever ask of a multi-page book on a clean device pays the probes,
        // not the full-book fetch). ONE MORE TRIGGER beside the probes, with its
        // HONEST COST stated (code-review F3): FindResumeTrackIndex also reads
        // the device queue's ItemPositionState, and the JF-581 shape (a
        // server-side UserData write loss with the position surviving only in
        // the device queue) would be invisible to the probes, so a device queue
        // holding ANY positioned entry releases the fetch. Because the position
        // store is written by EVERY qualifying stop (plain songs included) and
        // survives restarts, a household that has played anything holds entries
        // forever: on such devices the book gate degrades to the pre-JF-797
        // unconditional fetch (one unpaged query over a BOUNDED chapter list,
        // the addendum's row-volume concern is the album path, which has no
        // queue tier and discriminates unconditionally). Scoping the store to
        // book-shaped entries is the filed follow-up, JF-812; an under-fire here
        // would drop a real resume, the worse side of the trade.
        // The single-file shapes never reach here (their page is
        // the whole book by construction). The in-memory positioned-entry check
        // runs before the probes (cheapest first), and the whole gate folds into
        // one condition: nothing below it runs when the page answer is complete
        // or the probes prove no row carries resume-relevant user data.
        bool queueHoldsPositionedEntry = false;
        bool resumeProbeHit = false;

        // JF-790: the deep-resume gate is MOOT in the detected shape (the full
        // book is already in hand and the resume fork above already scanned every
        // chapter of it), so the whole gate section stays cold there.
        bool deepResumeEligible = fileNameOrderedBook == null && resumeTicks == 0 && continuationHasMore;

        if (deepResumeEligible)
        {
            // Gate-marker tail F4: the JF-581 valve is evaluated LAZILY, only once
            // the page answer is incomplete - the eager shape paid a lock acquisition
            // plus an O(cap) Values scan on _launchScopeLock for every single-page
            // book and every page-resolved resume, the shapes that discard the value.
            queueHoldsPositionedEntry = queueManager != null
                && session.DeviceId != null
                && queueManager.HasAnyStoredPosition(session.DeviceId);
        }

        if (deepResumeEligible && !queueHoldsPositionedEntry)
        {
            resumeProbeHit = await QueueContinuationFetcher.MayHaveResumeRelevantUserDataAsync(
                (probeStartIndex, probeLimit) => QueueContinuationFetcher.BuildScopedAudiobookChaptersQuery(
                    jellyfinUser, user, libraryManager, logger, book.Id, probeStartIndex, probeLimit),
                probeQuery => RetryHelper.ExecuteWithRequestBudgetAsync(
                    () => SearchService.SafeGetItemsResult(libraryManager, probeQuery, logger),
                    logger,
                    "GetBookTracksResumeProbe",
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

        }

        // Gate-marker tail F1: the gate DECISION logged for triage (the Debug
        // Logging Policy) on every path where the gate question was asked - the
        // first cut logged only inside the probe branch, so the JF-581
        // valve-release shape (a positioned queue entry releasing the fetch with
        // no probe run) logged nothing, and the 'positioned queue entries' field
        // was constant-false wherever the log did fire.
        if (deepResumeEligible)
        {
            logger.LogDebug(
                "{Label}: deep-resume gate for '{BookName}': page answer carries no position and more pages remain; released by {Trigger}",
                logLabel,
                book.Name,
                queueHoldsPositionedEntry ? "a positioned queue entry (JF-581 valve)"
                    : resumeProbeHit ? "a user-data probe hit"
                    : "nothing (staying cold)");
        }

        if (deepResumeEligible
            && (queueHoldsPositionedEntry || resumeProbeHit))
        {
            QueryResult<BaseItem> fullBook = await FetchUnpagedBookAsync("GetBookTracksDeepResume").ConfigureAwait(false);

            (int deepIndex, long deepTicks) = ResumeMath.FindResumeTrackIndex(
                fullBook.Items, jellyfinUser, userDataManager, queueManager, session.DeviceId, resumePosition: true, logger);
            if (deepIndex > 0)
            {
                RePageFullBookAt(fullBook.Items, deepIndex);
                resumeTicks = deepTicks;
            }
        }

        int offsetMs = 0;

        if (startIndex > 0 || resumeTicks > 0)
        {
            offsetMs = (int)TimeSpan.FromTicks(resumeTicks).TotalMilliseconds;

            logger.LogInformation(
                "{Label}: resuming '{Book}' from track {Index} ({TrackName}) at {OffsetMs}ms",
                logLabel, book.Name,
                startIndex,
                trackItems[startIndex].Name,
                offsetMs);
        }
        else
        {
            logger.LogInformation("{Label}: starting '{Book}' from the beginning (no resume position found)", logLabel, book.Name);
        }

        // JF-693 (the JF-687 refusal-before-ledger policy extended to the queue and
        // session writes): the launch branches below can answer the empty-secret
        // configuration Tell instead of a directive (the audiobook concat URL is
        // always token-gated), so the now-playing queue, the device queue and the
        // progressive-continuation record are applied AFTER the launch verdict: a
        // refused launch must not leave MediaInfo answering "playing <book>" with
        // nothing playing and a stale QueueContinuation that survives.
        void ApplyBookPlaybackState()
        {
            List<QueueItem> queueItems = new();
            for (int i = startIndex; i < trackItems.Count; i++)
            {
                queueItems.Add(new QueueItem { Id = trackItems[i].Id });
            }

            session.NowPlayingQueue = queueItems;
            session.FullNowPlayingItem = trackItems[startIndex];

            queueManager?.SetQueue(
                context.System.Device.DeviceID,
                trackItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
                0);

            // Store continuation info so PlaybackNearlyFinished can fetch the rest.
            // StartIndex counts the chapters the queue has consumed (page start +
            // page count; the database offset is independent of the resume slice,
            // and the JF-793 deep resume rebases it to the position-holding chapter's
            // page). JF-790: in the detected untagged/tie shape the continuation
            // pages the SORTED full book in memory (CachedTracks); the DB order
            // answered wrong once and must never serve the tail.
            if (continuationHasMore)
            {
                QueueContinuationStore.Set(
                    session.UserId,
                    context.System.Device.DeviceID,
                    new QueueContinuation
                    {
                        SourceType = "Audiobook",
                        ParentId = book.Id,
                        StartIndex = continuationStartIndex,
                        TotalCount = continuationTotalCount,
                        UserId = jellyfinUser.Id,
                        CachedTracks = fileNameOrderedBook,
                        // JF-674: bind the entry to THIS queue page (the ids just
                        // installed into session.NowPlayingQueue) so a later
                        // different-queue playback discards it at fetch time. Minted
                        // on ALL launch arms, VideoApp included: the identity
                        // validation already discards the entry for any later queue
                        // that is not the book page, while the chapter-relative
                        // cold-tracker resume fallthrough (ResumeIntentHandler) can
                        // still legitimately serve it through an AudioPlayer chapter
                        // relaunch of the SAME queue.
                        MintedQueueItemIds = QueueContinuation.QueueIdsOf(queueItems)
                    });
            }
        }

        string itemId = trackItems[startIndex].Id.ToString();

        // Native controls for books: play via the VideoApp concat stream (seek bar).
        // With a TRACKED position, resume via the ?start= slice (the tracker records
        // the book-absolute concat timeline); a cold tracker falls through to the flat
        // chapter resume (a chapter-relative position cannot slice the book timeline).
        if (Plugin.Instance?.Configuration?.NativeControlsForBooks == true)
        {
            // JF-794 blocker 2: the ONE verdict-aware key (a collapsed book under a
            // shared container reads its OWN leaf key, not the container key every
            // sibling book writes).
            string bookKey = AudiobookItems.ResolveTrackedBookKey(trackItems[startIndex], libraryManager);
            // Review major (JF-567): only the TRACKER's book-timeline position may
            // slice the concat playlist; the FindResumeTrackIndex fallback is
            // CHAPTER-relative and would land mid-chapter-1 on the book timeline.
            // A cold tracker falls through to the flat AudioPlayer chapter resume.
            long trackedTicks = Plugin.Instance?.AudiobookPositionTracker?.GetPositionTicks(bookKey) ?? 0;
            if (trackedTicks > 0)
            {
                // JF-567: VideoApp.Launch responses must OMIT shouldEndSession (the repo
                // reference rule; BuildAudiobookResumeResponse keeps it null).
                // JF-699 item 1: the builder either threw the StreamTokenNotConfigured
                // refusal (RequestPipeline answers it; nothing below runs) or delivered
                // the launch, so the JF-693 verdict wrapper is gone and the state/announce
                // writes simply follow the launch.
                SkillResponse trackedResponse = launch.BuildAudiobookResumeResponse(trackItems[startIndex], trackedTicks, user, context, libraryManager);

                ApplyBookPlaybackState();
                PlaybackLaunchBuilder.AttachAnnounceIfLaunched(
                    trackedResponse,
                    SpeechBuilder.BuildOutputSpeech(
                        "ResumingBookSsml", "ResumingBook", locale, book.Name, trackItems[startIndex].Name));
                return trackedResponse;
            }

            // Cold tracker: resumeTicks == 0 covers TWO shapes (a genuinely fresh
            // book, and the (lastPlayedIndex+1, 0) shape where earlier chapters are
            // fully played and the next is unstarted; FindResumeTrackIndex returns
            // ticks 0 there, and startIndex > 0 is silently discarded by the fresh
            // launch). Both keep the fresh VideoApp launch (the flag exists for
            // the seek bar); a chapter WITH progress falls through to the flat
            // chapter resume below, where the chapter-relative position is honest
            // on the chapter's own timeline (the same discipline the LaunchRequest
            // resume offer applies).
            if (resumeTicks <= 0)
            {
                SkillResponse freshResponse = await launch.BuildAudiobookVideoAppLaunchResponseAsync(
                    itemId,
                    trackItems[startIndex],
                    SpeechBuilder.BuildNowPlayingSpeech(book.Name, locale, launch.GetAnnounceNowPlaying(user)),
                    user,
                    context,
                    request,
                    libraryManager).ConfigureAwait(false);

                // JF-699 item 1: throw-or-launch (see the tracked arm above).
                ApplyBookPlaybackState();
                return freshResponse;
            }
        }

        SkillResponse standardResponse = launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, launch.GetStreamUrl(itemId, user), itemId, trackItems[startIndex], user, context, offsetMs, libraryManager: libraryManager);

        // JF-699 item 1: throw-or-launch (see the tracked arm above); the state
        // writes and the resume announce simply follow the launch.
        ApplyBookPlaybackState();

        // Add resume announcement when not starting from the beginning
        if (startIndex > 0 || resumeTicks > 0)
        {
            PlaybackLaunchBuilder.AttachAnnounceIfLaunched(
                standardResponse,
                SpeechBuilder.BuildOutputSpeech(
                    "ResumingBookSsml", "ResumingBook", locale, book.Name, trackItems[startIndex].Name));
            standardResponse.Response.ShouldEndSession = true;
        }

        return standardResponse;
    }
}

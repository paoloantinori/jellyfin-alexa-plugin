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
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-455: the playlist query must NOT carry the per-user library filter. Playlists
/// are user-scoped (query.User gates visibility) and native playlists live outside any
/// media library, so any TopParentIds restriction excluded them all for restricted users.
/// </summary>
[Collection("Plugin")]
public class PlayPlaylistIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayPlaylistIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    // JF-808: the recording subclass (the JF-807 PlayBook shape) so the warming
    // pins can assert the no-progressive-before-refusal half of the
    // gate-before-announcement contract through the progressive capture seam.
    private sealed class RecordingPlayPlaylistHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null)
        : PlayPlaylistIntentHandler(sessionManager, config, libraryManager, userManager, loggerFactory, artistIndex: artistIndex)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private RecordingPlayPlaylistHandler CreateHandler(IArtistIndex? artistIndex = null)
    {
        return new RecordingPlayPlaylistHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory,
            artistIndex);
    }

    private static IntentRequest CreateRequest(string playlistName = "road trip songs", string intentName = IntentNames.PlayPlaylist)
    {
        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = intentName,
                Slots = new Dictionary<string, Slot>
                {
                    ["playlist"] = new Slot { Name = "playlist", Value = playlistName }
                }
            },
            Locale = "en-US",
            RequestId = "test-req"
        };
    }

    [Fact]
    public async Task PlayPlaylist_RestrictedUser_DoesNotSetTopParentIdsOnQuery()
    {
        // A CollectionFolder that WOULD resolve to a physical folder id: if the old
        // ApplyLibraryFilter call were still on this path, TopParentIds would be set.
        var cfId = Guid.NewGuid();
        var physicalId = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser();
        user.AllowedLibraryIds = new List<string> { cfId.ToString() };

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());

        var cf = new CollectionFolder { Id = cfId };
        cf.PhysicalLocationsList = new[] { "/data/media/music" };
        _libraryManagerMock.Setup(l => l.GetItemById(cfId)).Returns(cf);
        _libraryManagerMock.Setup(l => l.FindByPath("/data/media/music", true))
            .Returns(new Folder { Id = physicalId });

        InternalItemsQuery? primaryQuery = null;
        InternalItemsQuery? fuzzyQuery = null;
        _libraryManagerMock
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => primaryQuery = q)
            .Returns(new QueryResult<BaseItem>());
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => fuzzyQuery = q)
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateRequest(),
            TestHelpers.CreateTestContext(),
            user,
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(primaryQuery);
        Assert.Empty(primaryQuery.TopParentIds); // no library filter on the playlist query

        Assert.NotNull(fuzzyQuery);
        Assert.Empty(fuzzyQuery.TopParentIds); // nor on the fuzzy fallback
    }

    [Fact]
    public async Task PlayPlaylist_CalledCarrierInSlot_SpeechEchoesStrippedName()
    {
        // JF-602 AC#3: the play paths keep the read-time strip; the not-found echo
        // must name the stripped playlist, never the carrier-polluted raw fill.
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>());
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var response = await CreateHandler().HandleAsync(
            CreateRequest(playlistName: "called road trip songs"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        var speech = ((global::Alexa.NET.Response.PlainTextOutputSpeech)response.Response.OutputSpeech).Text;
        Assert.Contains("road trip songs", speech, StringComparison.Ordinal);
        Assert.DoesNotContain("called road trip songs", speech, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShufflePlay_CalledCarrierInSlot_SpeechEchoesStrippedName()
    {
        // JF-602 AC#3 sibling pin for ShufflePlay: the strip reaches the shared
        // AlbumPlay lookup through the same read-time normalization.
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>());
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new ShufflePlayIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            CreateRequest("called road trip songs", IntentNames.ShufflePlay),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        var speech = ((global::Alexa.NET.Response.PlainTextOutputSpeech)response.Response.OutputSpeech).Text;
        Assert.Contains("road trip songs", speech, StringComparison.Ordinal);
        Assert.DoesNotContain("called road trip songs", speech, StringComparison.Ordinal);
    }

    // JF-526 (JF-508 sibling): BuildPlaylistPlayResponseAsync's site-level FuzzyMatch
    // pre-check (the >1-match branch) returns before HandleFuzzyMiss, so without the
    // shared gate a 2-word partial-coverage hit ("soul coffee" -> "Starfish & Coffee",
    // score 72) auto-played ungated; the gated miss now takes the HandleFuzzyMiss
    // yes/no prompt.
    [Fact]
    public async Task PlayPlaylist_TwoWordPartialCoverageFuzzyHit_PromptsInsteadOfAutoPlaying()
    {
        using var statics = StubBaseItemStatics();
        SetupTwoPlaylistCandidates();

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateRequest(playlistName: "soul coffee"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"),
            "the playlist pre-check must not auto-play a partial-coverage short query (JF-526)");
    }

    [Fact]
    public async Task PlayPlaylist_TwoWordFullCoverageFuzzyHit_StillPassesThePreCheck()
    {
        // Counter-case: full coverage keeps the pre-check acceptance. The Folder
        // stand-ins have no resolvable tracks, so the normal flow surfaces the
        // empty-playlist Tell (session-ending, no disambiguation ask), which is only
        // reachable when the pre-check accepted the query.
        using var statics = StubBaseItemStatics();
        SetupTwoPlaylistCandidates();

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateRequest(playlistName: "coffee tv"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.False(response.SessionAttributes?.ContainsKey("disambig_matches") == true);
    }

    // ========== JF-808: the playlist play path's Layer-1 warming gate (the ASK, both callers of the shared builder) ==========

    /// <summary>
    /// The ONE resolvable-playlist fixture for the JF-808 warming pins
    /// (TestHelpers.SetupPlaylist, hoisted with the shuffle suite's former private
    /// copy so the 12.x linked-child mock contract has one owner), so the pre-fix
    /// RED failure of the entry-gate pins is the clean no-throw (the ungated
    /// query ran and played), not a mock-default null crashing the handler.
    /// </summary>
    private void SetupPlaylistPlay()
        => TestHelpers.SetupPlaylist(_libraryManagerMock, _userManagerMock);

    /// <summary>
    /// JF-808 RED PROOF (the warming axis, the ASK): a playlist ask running while
    /// the artist index is still loading (the post-restart window) must refuse at
    /// entry, before the shared builder's cold surface (the SearchTerm playlist
    /// query on its RetryAsync channel, the fuzzy fallback, and the
    /// GetManageableItems whole-track resolution), instead of running it inside
    /// Alexa's ~8s window. Pre-JF-808 the playlist play path was warming-ungated
    /// END TO END (this ask, its ShufflePlay twin, and the YesIntent confirm arm),
    /// the roster omission this task makes a deliberate gated row.
    /// INDEX CHOICE: playlists have no in-memory index of their own (neither the
    /// artist nor the song n-gram index serves Playlist items), so this is a
    /// stand-in gate; the ARTIST index is the family's established stand-in (the
    /// PlayAlbum Layer-1 precedent, joined by books in JF-807) and keeps the ask
    /// and its confirm arm symmetric (a song-index stand-in would make the two
    /// diverge whenever the indexes' readiness windows differ). The sibling the
    /// playlist path most resembles, the album ask, gates the same index.
    /// ANNOUNCEMENT CONTRACT, honestly scoped: this path sends NO pre-query
    /// "searching" progressive announcement (unlike PlayBook's SearchingBook), so
    /// the F5 ready-side announcement pin has nothing to observe; the empty half
    /// of the contract is still enforced below (no progressive text may precede
    /// the refusal).
    /// </summary>
    [Fact]
    public async Task HandleAsync_PlaylistAsk_WhileIndexWarming_ThrowsAtEntry()
    {
        var handler = CreateHandler(TestHelpers.WarmingArtistIndex());
        using var statics = StubBaseItemStatics();
        SetupPlaylistPlay();

        var ex = await Assert.ThrowsAsync<SkillWarmingUpException>(() =>
            handler.HandleAsync(
                CreateRequest(),
                TestHelpers.CreateTestContext(),
                TestHelpers.CreateTestUser(jellyfinToken: "tok"),
                TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
                CancellationToken.None));
        Assert.StartsWith("artist", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The no-progressive-before-refusal half of the placement contract (the
        // JF-807 F1 shape): a gate moved below any future announcement would still
        // throw and otherwise keep this pin green while the user hears speech first.
        Assert.Equal(string.Empty, handler.Progressive.AllText);
    }

    /// <summary>
    /// JF-808 companion pin (gate transparency): a READY artist index leaves the
    /// playlist ask unchanged; the gate only converts the warming window, never
    /// the warm path (the JF-807 ask-twin idiom).
    /// </summary>
    [Fact]
    public async Task HandleAsync_PlaylistAsk_ReadyIndex_PlaysUnchanged()
    {
        var handler = CreateHandler(TestHelpers.ReadyArtistIndex());
        using var statics = StubBaseItemStatics();
        SetupPlaylistPlay();

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives!));
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// The shuffle-ask construction shared by the JF-808 pins (the shuffle twin
    /// rides the plain handler: no progressive capture is asserted on this side).
    /// </summary>
    private ShufflePlayIntentHandler CreateShuffleHandler(IArtistIndex? artistIndex = null)
        => new ShufflePlayIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory,
            artistIndex: artistIndex);

    /// <summary>
    /// JF-808 sibling pin (the ShufflePlay twin, the JF-602 AC#3 placement
    /// convention): ShufflePlayIntentHandler shares this builder and its whole
    /// cold surface verbatim (only the shuffle flag differs), so its ask carries
    /// the same gate and refuses in the same warming window.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ShufflePlayAsk_WhileIndexWarming_ThrowsAtEntry()
    {
        var handler = CreateShuffleHandler(TestHelpers.WarmingArtistIndex());
        using var statics = StubBaseItemStatics();
        SetupPlaylistPlay();

        var ex = await Assert.ThrowsAsync<SkillWarmingUpException>(() =>
            handler.HandleAsync(
                CreateRequest(intentName: IntentNames.ShufflePlay),
                TestHelpers.CreateTestContext(),
                TestHelpers.CreateTestUser(jellyfinToken: "tok"),
                TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
                CancellationToken.None));
        Assert.StartsWith("artist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-808 code-review F2 (the ShufflePlay ready-path twin): the throw pin
    /// alone cannot tell a correctly gated ask from an INVERTED readiness check
    /// (an always-throwing gate keeps it green while every shuffle ask is
    /// refused forever), so the shuffle side pins gate transparency too, exactly
    /// like the PlayPlaylist ready pin and the JF-806 confirm twins.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ShufflePlayAsk_ReadyIndex_PlaysUnchanged()
    {
        var handler = CreateShuffleHandler(TestHelpers.ReadyArtistIndex());
        using var statics = StubBaseItemStatics();
        SetupPlaylistPlay();

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(intentName: IntentNames.ShufflePlay),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives!));
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// JF-808 code-review F4 (the ShufflePlay twin of the elicit-hatch pin): the
    /// duplicated elicit-then-gate boilerplate is pinned on BOTH handlers, so a
    /// refactor reordering the two in ShufflePlay alone cannot silently convert
    /// a slot-less shuffle ask into the session-ending warming refusal.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ShufflePlayAsk_WhileIndexWarming_EmptySlot_StillElicitsPlaylistName()
    {
        var handler = CreateShuffleHandler(TestHelpers.WarmingArtistIndex());

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(playlistName: string.Empty, intentName: IntentNames.ShufflePlay),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.Contains(
            "Which playlist would you like to hear?",
            ((global::Alexa.NET.Response.PlainTextOutputSpeech)response.Response.OutputSpeech).Text,
            StringComparison.Ordinal);
        Assert.False(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// JF-808 placement pin (the slot-elicitation hatch): the empty-slot
    /// DidNotCatchPlaylistName elicit sits BEFORE the warming gate, so a slot-less
    /// ask during the warming window still elicits the playlist name (mic open)
    /// instead of being converted to the warming refusal. The playlist path has
    /// NO feature flag gate (playlists are cross-type always-allowed, the JF-806
    /// decision), so the flag-gate-position discriminator has nothing to order
    /// against and the elicit-then-gate shape is the whole story; the cancel-word
    /// hatch above it stays reachable by the same placement.
    /// </summary>
    [Fact]
    public async Task HandleAsync_PlaylistAsk_WhileIndexWarming_EmptySlot_StillElicitsPlaylistName()
    {
        var handler = CreateHandler(TestHelpers.WarmingArtistIndex());

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(playlistName: string.Empty),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        // The localized VALUE, never the raw key (the F5 key-vs-value assert catch).
        Assert.Contains(
            "Which playlist would you like to hear?",
            ((global::Alexa.NET.Response.PlainTextOutputSpeech)response.Response.OutputSpeech).Text,
            StringComparison.Ordinal);
        Assert.False(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// The shared setup of the two candidate playlists the JF-526 pre-check tests
    /// fuzzy-match against. Tags must be non-null: BaseItem.GetInheritedTags does
    /// AddRange(Tags) with no null guard.
    /// </summary>
    private void SetupTwoPlaylistCandidates()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());

        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem>
                {
                    new Folder { Name = "Starfish & Coffee", Id = Guid.NewGuid(), Tags = Array.Empty<string>() },
                    new Folder { Name = "Coffee & TV", Id = Guid.NewGuid(), Tags = Array.Empty<string>() }
                },
                TotalRecordCount = 2
            });
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
    }

    /// <summary>
    /// The shared BaseItem statics stub scope (TestHelpers.StubBaseItemStatics,
    /// the hoisted former per-file copy; the off-host limitation it works around
    /// is the one DeviceQueueManagerTests documents for the track-resolution path).
    /// </summary>
    private IDisposable StubBaseItemStatics()
        => TestHelpers.StubBaseItemStatics(_libraryManagerMock);
}

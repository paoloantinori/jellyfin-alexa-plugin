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
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-658 characterization pins for the PlayArtistSongs search axes the JF-315
/// batch-6b gate table flagged as having ZERO direct coverage (rows 6, 13, 14,
/// and the parallel half of row 16): Fast mode's in-memory tier skip, Fast
/// mode's ungated single-query DB path, Thorough mode's PARALLEL DB tiers 2-4,
/// and the ASR compound-word variants on the Thorough DB tier 1. Written FIRST
/// against the pre-consolidation inline chain and required to stay green on the
/// consolidated <see cref="Util.ArtistSearch.SearchAsync"/> (the fold must
/// preserve all four behaviors; a red pin after the fold is a design violation,
/// not a fixture to update). The same four scenarios are pinned at the shared
/// layer by ArtistSearchTests' JF-658 facts; this file owns the end-to-end
/// handler seam (the wiring of the axes, not just the axes' semantics).
/// </summary>
[Collection("Plugin")]
public class PlayArtistSongsSearchModeAxisTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture(configure: c => c.AsrCompoundWordFixEnabled = false);
    private readonly Mock<IArtistIndex> _artistIndexMock = new();

    private static IntentRequest CreateIntentRequest(string musician)
    {
        var intent = new Intent { Name = IntentNames.PlayArtistSongs };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["musician"] = new Slot { Name = "musician", Value = musician }
        };
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Entities.User CreateFastUser()
        => TestHelpers.CreateTestUser(searchResponseMode: SearchResponseMode.Fast);

    private static string? PlayedTitle(SkillResponse response)
        => TestHelpers.GetPlayDirective(response)?.AudioItem?.Metadata?.Title;

    private PlayArtistSongsIntentHandler CreateHandler(IArtistIndex? artistIndex = null)
        => new PlayArtistSongsIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            artistIndex);

    /// <summary>
    /// Row 6 (Fast in-memory tier skip): the JF-437 'beatles live' shape under a
    /// Fast user must resolve through tier-4 fuzzy-all alone. Thorough mode's
    /// word-coverage tier surfaces The Beatles (pinned by
    /// PlayArtistSongsIntentHandlerTests.HandleAsync_BeatlesLiveQuery_PlaysTheBeatles_NeverEagles);
    /// Fast mode skips the prefix and word-coverage tiers by design, so the fuzzy
    /// window ranks the near-anagram Eagles higher and THAT is the Fast behavior
    /// being pinned (speed over recall, the documented mode contract).
    /// </summary>
    [Fact]
    public async Task HandleAsync_FastUser_InMemory_SkipsPrefixAndWordCoverageTiers()
    {
        var theBeatles = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var eagles = new MusicArtist { Name = "Eagles", Id = Guid.NewGuid() };
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(new List<BaseItem> { theBeatles, eagles });

        var beatlesSong = new Audio { Name = "Yesterday", Id = Guid.NewGuid() };
        var eaglesSong = new Audio { Name = "Hotel California", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) => q.ArtistIds.Contains(eagles.Id)
                ? new List<BaseItem> { eaglesSong }
                : new List<BaseItem> { beatlesSong });

        _fx.SetupUserMock();
        var handler = CreateHandler(_artistIndexMock.Object);

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest("beatles live"), _fx.CreateContext(), CreateFastUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession, "Fast mode auto-plays, never prompts");
        Assert.Equal("Hotel California", PlayedTitle(response));

        // The index served the whole search: no DB artist query is issued.
        _fx.LibraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.MusicArtist))), Times.Never);
    }

    /// <summary>
    /// Row 14 (Fast DB ungated tier 1): a direct long-name containment hit
    /// ('florence' matching 'Florence + The Machine', 15 characters beyond the
    /// JF-381 band) PLAYS under Fast, and the DB path issues exactly ONE artist
    /// query (the SearchTerm): no containment-band gate (there is no recovery
    /// tier to hand off to in Fast) and no fallback tiers.
    /// </summary>
    [Fact]
    public async Task HandleAsync_FastUser_ColdDb_SingleSearchTermQuery_PlaysUngatedLongName()
    {
        var fftm = new MusicArtist { Name = "Florence + The Machine", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Shake It Out", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
                q.ArtistIds != null && q.ArtistIds.Length > 0
                    ? new List<BaseItem> { song }
                    : new List<BaseItem> { fftm });

        _fx.SetupUserMock();
        var handler = CreateHandler(artistIndex: null); // cold: database path

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest("florence"), _fx.CreateContext(), CreateFastUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession, "the ungated direct hit auto-plays under Fast");
        Assert.Equal("Shake It Out", PlayedTitle(response));

        // Exactly one artist query, and it is the SearchTerm shape: no band-gated
        // re-query, no NameStartsWith/NameContains fallback tier.
        _fx.LibraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.MusicArtist))), Times.Once);
        _fx.LibraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.SearchTerm == "florence"
            && q.NameStartsWith == null
            && q.NameContains == null)), Times.Once);
    }

    /// <summary>
    /// Row 16 parallel half (Thorough DB tiers 2-4): the NameContains tier-4
    /// query IS issued even though the NameStartsWith tier-2 query already has a
    /// match (the sequential short-circuit would skip it), and the tier-2 winner
    /// keeps priority over a tier-4 candidate. This is the cold-window parallel
    /// structure the live traffic was tuned on (kept verbatim by the JF-658 fold:
    /// the tier tasks' retry backoffs overlap, while the synchronous queries
    /// themselves share the request thread).
    /// </summary>
    [Fact]
    public async Task HandleAsync_ThoroughUser_ColdDb_Tiers2To4RunInParallel_Tier2KeepsPriority()
    {
        var abba = new MusicArtist { Name = "Abba", Id = Guid.NewGuid() };
        var tribute = new MusicArtist { Name = "Abba Tribute Band", Id = Guid.NewGuid() };
        var abbaSong = new Audio { Name = "Dancing Queen", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                {
                    return new List<BaseItem> { abbaSong };
                }

                if (q.SearchTerm != null)
                {
                    return new List<BaseItem>(); // tier 1 misses
                }

                if (q.NameStartsWith == "abba")
                {
                    return new List<BaseItem> { abba }; // tier 2 hits
                }

                if (q.NameStartsWith == "abba live")
                {
                    return new List<BaseItem>(); // tier 3 misses
                }

                if (q.NameContains == "abba live")
                {
                    return new List<BaseItem> { tribute }; // tier 4 hits a lower-priority candidate
                }

                return new List<BaseItem>();
            });

        _fx.SetupUserMock();
        var handler = CreateHandler(artistIndex: null); // cold: database path

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest("abba live"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Dancing Queen", PlayedTitle(response)); // tier 2's Abba, never the tribute

        // The parallel proof: tier 4's contains query ran DESPITE tier 2's hit.
        _fx.LibraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.NameContains == "abba live")), Times.Once);
        _fx.LibraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.NameStartsWith == "abba")), Times.Once);
        _fx.LibraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.NameStartsWith == "abba live")), Times.Once);
    }

    /// <summary>
    /// Row 13 (ASR variants on the Thorough DB tier 1): when the compound-word
    /// fix is enabled and the original SearchTerm query returns nothing, the
    /// joined-word variant ('lazy bones' retried as 'lazybones') runs INSIDE
    /// tier 1 and its result plays. Fast mode suppresses the variants (the
    /// wrapper's mode policy); this pin locks the Thorough leg.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ThoroughUser_ColdDb_Tier1RetriesAsrCompoundWordVariant()
    {
        _fx.Config.AsrCompoundWordFixEnabled = true;
        var lazybones = new MusicArtist { Name = "Lazybones", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Lazybones Blues", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
                q.ArtistIds != null && q.ArtistIds.Length > 0
                    ? new List<BaseItem> { song }
                    : q.SearchTerm == "lazybones"
                        ? new List<BaseItem> { lazybones }
                        : new List<BaseItem>());

        _fx.SetupUserMock();
        var handler = CreateHandler(artistIndex: null); // cold: database path

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest("lazy bones"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession, "the variant hit auto-plays");
        Assert.Equal("Lazybones Blues", PlayedTitle(response));

        // The original ran and missed; the joined variant ran and hit, both as
        // tier-1 SearchTerm queries (no prefix/contains fallback tier involved).
        _fx.LibraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SearchTerm == "lazy bones")), Times.Once);
        _fx.LibraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SearchTerm == "lazybones")), Times.Once);
        _fx.LibraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.NameStartsWith != null || q.NameContains != null)), Times.Never);
    }
}

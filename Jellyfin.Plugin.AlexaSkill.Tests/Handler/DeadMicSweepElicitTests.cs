using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-550 (dead-mic sweep): every converted empty-slot branch must ASK with the
/// mic open - Dialog.ElicitSlot on the right slot of the right intent, open
/// session, reprompt - never the Tell-question shape of the JF-549 incident.
/// The elicit branches return before any dependency is touched, so the handlers
/// are constructed with bare mocks.
/// </summary>
public class DeadMicSweepElicitTests : PluginTestBase
{
    private static IntentRequest Request(string intentName, params (string Slot, string? Value)[] slots)
    {
        var intent = new Intent { Name = intentName, Slots = new Dictionary<string, Slot>() };
        foreach (var (slot, value) in slots)
        {
            // Real Alexa requests always carry every model slot (absent = null
            // value); some handlers read via the indexer, so the entry must exist.
            intent.Slots[slot] = new Slot { Name = slot, Value = value };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "sweep-req", DialogState = "COMPLETED" };
    }

    private static Context Context() => TestHelpers.CreateTestContext();

    private static SessionInfo Session(ISessionManager sessionManager) =>
        TestHelpers.CreateTestSession(sessionManager, NullLoggerFactory.Instance);

    private static Entities.User User() => TestHelpers.CreateTestUser();

    private static void AssertElicit(SkillResponse response, string slot, string intent)
    {
        Assert.NotNull(response);
        TestHelpers.AssertSessionOpen(response, "a question must keep the session open or the mic never listens");
        Assert.NotNull(response.Response.Reprompt);
        var elicit = response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot") as ElicitSlotDirective;
        Assert.NotNull(elicit);
        Assert.Equal(slot, elicit!.SlotToElicit);
        Assert.Equal(intent, elicit.UpdatedIntent.Name);
    }

    private static async Task AssertElicitsAsync(
        Func<Task<SkillResponse>> act,
        string slot,
        string intent)
    {
        SkillResponse response = await act();
        AssertElicit(response, slot, intent);
    }

    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly PluginConfiguration _config = new();

    [Fact]
    public Task SleepTimer_EmptyDuration_ElicitsDurationMinutes()
        => AssertElicitsAsync(
            () => new SleepTimerIntentHandler(_sessionManager.Object, _config, NullLoggerFactory.Instance)
                .HandleAsync(Request(IntentNames.SleepTimer), Context(), User(), Session(_sessionManager.Object), CancellationToken.None),
            "sleep_duration", IntentNames.SleepTimer);

    [Fact]
    public Task SetReminder_EmptyTime_ElicitsReminderTime()
    {
        Context context = Context();
        context.System.ApiAccessToken = "test-api-access-token";
        return AssertElicitsAsync(
            () => new SetReminderIntentHandler(_sessionManager.Object, _config, NullLoggerFactory.Instance)
                .HandleAsync(Request(IntentNames.SetReminder), context, User(), Session(_sessionManager.Object), CancellationToken.None),
            "reminder_time", IntentNames.SetReminder);
    }

    [Fact]
    public Task AddToQueue_EmptySong_ElicitsSong()
        => AssertElicitsAsync(
            () => new AddToQueueIntentHandler(_sessionManager.Object, _config, Mock.Of<ILibraryManager>(), Mock.Of<IUserManager>(), NullLoggerFactory.Instance)
                .HandleAsync(Request(IntentNames.AddToQueue, ("song", null), ("musician", null)), Context(), User(), Session(_sessionManager.Object), CancellationToken.None),
            "song", IntentNames.AddToQueue);

    [Fact]
    public Task AddToQueue_CapturedCancelWord_EndsSessionWithoutSearching()
    {
        var library = new Mock<ILibraryManager>();
        var handler = new AddToQueueIntentHandler(_sessionManager.Object, _config, library.Object, Mock.Of<IUserManager>(), NullLoggerFactory.Instance);
        var request = Request(IntentNames.AddToQueue, ("song", "stop"), ("musician", null));
        request.DialogState = "IN_PROGRESS";

        return Task.Run(async () =>
        {
            SkillResponse response = await handler.HandleAsync(request, Context(), User(), Session(_sessionManager.Object), CancellationToken.None);
            Assert.NotNull(response);
            Assert.True(response.Response.ShouldEndSession);
            Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
            library.VerifyNoOtherCalls();
        });
    }

    [Fact]
    public Task PlayNext_EmptySong_ElicitsSong()
        => AssertElicitsAsync(
            () => new PlayNextIntentHandler(_sessionManager.Object, _config, Mock.Of<ILibraryManager>(), Mock.Of<IUserManager>(), NullLoggerFactory.Instance)
                .HandleAsync(Request(IntentNames.PlayNext, ("song", null), ("musician", null)), Context(), User(), Session(_sessionManager.Object), CancellationToken.None),
            "song", IntentNames.PlayNext);

    [Fact]
    public Task QueryArtistLibrary_EmptyMusician_ElicitsMusician()
        => AssertElicitsAsync(
            () => new QueryArtistLibraryIntentHandler(_sessionManager.Object, _config, Mock.Of<ILibraryManager>(), Mock.Of<IUserManager>(), Mock.Of<MediaBrowser.Controller.Library.IUserDataManager>(), NullLoggerFactory.Instance)
                .HandleAsync(Request(IntentNames.QueryArtistLibrary), Context(), User(), Session(_sessionManager.Object), CancellationToken.None),
            "musician", IntentNames.QueryArtistLibrary);

    [Fact]
    public Task PlayMoodMusic_EmptyMood_ElicitsMood()
        => AssertElicitsAsync(
            () => new PlayMoodMusicIntentHandler(_sessionManager.Object, _config, Mock.Of<ILibraryManager>(), Mock.Of<IUserManager>(), Mock.Of<MediaBrowser.Controller.Library.IUserDataManager>(), NullLoggerFactory.Instance)
                .HandleAsync(Request(IntentNames.PlayMoodMusic), Context(), User(), Session(_sessionManager.Object), CancellationToken.None),
            "mood", IntentNames.PlayMoodMusic);

    [Fact]
    public Task PlayPlaylist_EmptyName_ElicitsPlaylist()
        => AssertElicitsAsync(
            () => new PlayPlaylistIntentHandler(_sessionManager.Object, _config, Mock.Of<ILibraryManager>(), Mock.Of<IUserManager>(), NullLoggerFactory.Instance)
                .HandleAsync(Request(IntentNames.PlayPlaylist), Context(), User(), Session(_sessionManager.Object), CancellationToken.None),
            "playlist", IntentNames.PlayPlaylist);

    [Fact]
    public Task ShufflePlay_EmptyName_ElicitsPlaylist()
        => AssertElicitsAsync(
            () => new ShufflePlayIntentHandler(_sessionManager.Object, _config, Mock.Of<ILibraryManager>(), Mock.Of<IUserManager>(), NullLoggerFactory.Instance)
                .HandleAsync(Request(IntentNames.ShufflePlay), Context(), User(), Session(_sessionManager.Object), CancellationToken.None),
            "playlist", IntentNames.ShufflePlay);

    [Fact]
    public Task RateItem_EmptyStars_ElicitsStarRating()
        => AssertElicitsAsync(
            () => new RateItemIntentHandler(_sessionManager.Object, _config, Mock.Of<MediaBrowser.Controller.Library.IUserDataManager>(), Mock.Of<IUserManager>(), Mock.Of<ILibraryManager>(), NullLoggerFactory.Instance)
                .HandleAsync(Request(IntentNames.RateItem, ("star_rating", null)), Context(), User(), Session(_sessionManager.Object), CancellationToken.None),
            "star_rating", IntentNames.RateItem);

    [Fact]
    public Task RateItem_CapturedCancelWord_EndsSessionWithoutWriting()
    {
        var userData = new Mock<MediaBrowser.Controller.Library.IUserDataManager>();
        var handler = new RateItemIntentHandler(_sessionManager.Object, _config, userData.Object, Mock.Of<IUserManager>(), Mock.Of<ILibraryManager>(), NullLoggerFactory.Instance);
        var request = Request(IntentNames.RateItem, ("star_rating", "stop"));
        request.DialogState = "IN_PROGRESS";

        return Task.Run(async () =>
        {
            SkillResponse response = await handler.HandleAsync(request, Context(), User(), Session(_sessionManager.Object), CancellationToken.None);
            Assert.NotNull(response);
            Assert.True(response.Response.ShouldEndSession);
            Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
            userData.VerifyNoOtherCalls();
        });
    }

    [Fact]
    public Task BrowseLibrary_GenresWithoutFilter_ElicitsCategory()
    {
        var userManager = new Mock<IUserManager>();
        userManager.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
        var handler = new BrowseLibraryIntentHandler(_sessionManager.Object, _config, Mock.Of<ILibraryManager>(), userManager.Object, NullLoggerFactory.Instance);

        return AssertElicitsAsync(
            () => handler.HandleAsync(
                Request(IntentNames.BrowseLibrary, ("browse_category", "genres")),
                Context(), User(), Session(_sessionManager.Object), CancellationToken.None),
            "browse_category", IntentNames.BrowseLibrary);
    }
}

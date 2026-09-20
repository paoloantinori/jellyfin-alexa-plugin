using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using AlexaSession = Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class DynamicEntitiesInterceptorTests : PluginTestBase
{
    private readonly Mock<DynamicEntityBuilder> _builderMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public DynamicEntitiesInterceptorTests()
    {
        _builderMock = new Mock<DynamicEntityBuilder>(
            Mock.Of<MediaBrowser.Controller.Library.ILibraryManager>(),
            Mock.Of<MediaBrowser.Controller.Library.IUserManager>(),
            LoggerFactory.Create(b => b.AddDebug()).CreateLogger<DynamicEntityBuilder>(),
            null!);

        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => b.AddDebug());
    }

    private DynamicEntitiesInterceptor CreateInterceptor()
    {
        return new DynamicEntitiesInterceptor(
            _builderMock.Object,
            _config,
            Mock.Of<MediaBrowser.Controller.Library.ILibraryManager>(),
            _loggerFactory.CreateLogger<DynamicEntitiesInterceptor>());
    }

    private RequestContext CreateContext(
        Request? request = null,
        Context? alexaContext = null,
        AlexaSession? session = null)
    {
        request ??= new IntentRequest { Type = "IntentRequest" };
        alexaContext ??= new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };

        var handler = new Mock<BaseHandler>(
            Mock.Of<MediaBrowser.Controller.Session.ISessionManager>(),
            _config,
            _loggerFactory);
        handler.Setup(h => h.CanHandle(It.IsAny<Request>())).Returns(true);

        var ctx = new RequestContext(request, alexaContext, session, handler.Object);
        ctx.Response = ResponseBuilder.Tell("test");
        return ctx;
    }

    [Fact]
    public async Task ProcessAsync_NotNewSession_DoesNotInjectDirective()
    {
        var interceptor = CreateInterceptor();
        var request = new IntentRequest { Type = "IntentRequest" };
        var session = new AlexaSession { New = false };
        var ctx = CreateContext(request, session: session);

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        // Should not have called builder (intent name is "none", not TV or book context)
        _builderMock.Verify(
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);
    }

    /// <summary>
    /// Review round 2 (2026-09-20): a bound device plus a stale token id (config
    /// wiped, JF-588 shape) must FAIL CLOSED. The first fix NRE'd; the second fix
    /// would have failed OPEN (unrestricted values on a bound device, riding the
    /// same user-not-found Tell). The contract now mirrors the funnel: no plugin
    /// user, no entity refresh at all.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_BoundDevice_StaleUserId_SkipsEntityRefreshWithoutThrowing()
    {
        _config.DeviceLibraryBindings.Add(new Jellyfin.Plugin.AlexaSkill.Configuration.DeviceLibraryBinding
        {
            DeviceId = "test-device",
            LibraryId = Guid.NewGuid().ToString(),
        });

        var interceptor = CreateInterceptor();
        // CreateContext's default IS the stale-id shape: fresh random-Guid access
        // token, DeviceID "test-device", and PluginTestBase seeds no config users.
        var ctx = CreateContext(new LaunchRequest { Type = "LaunchRequest" }, session: new AlexaSession { New = false });
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);
    }

    /// <summary>
    /// Review round 2, cache-scope dimension: a resolved user on a BOUND device
    /// must reach Build with the bound library id as the cache-scope argument, so
    /// the output cache can never serve an unrestricted entry to a bound device.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_BoundDevice_ResolvedUser_BuildsScopedToBoundLibrary()
    {
        var userId = Guid.NewGuid();
        var boundLibraryId = Guid.NewGuid().ToString();
        var user = new Jellyfin.Plugin.AlexaSkill.Entities.User { Id = userId };
        _config.Users.Add(user);
        _config.DeviceLibraryBindings.Add(new Jellyfin.Plugin.AlexaSkill.Configuration.DeviceLibraryBinding
        {
            DeviceId = "test-device",
            LibraryId = boundLibraryId,
        });
        var directive = new DynamicEntitiesDirective();
        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), boundLibraryId))
            .Returns(directive);

        var interceptor = CreateInterceptor();
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };

        var ctx = CreateContext(new LaunchRequest { Type = "LaunchRequest" }, alexaContext, session: new AlexaSession { New = false });
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Contains(directive, ctx.Response!.Response.Directives!);
    }

    [Fact]
    public async Task ProcessAsync_LaunchRequest_InjectsDirective()
    {
        var userId = Guid.NewGuid();
        _config.Users.Add(new Jellyfin.Plugin.AlexaSkill.Entities.User { Id = userId });
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(directive);

        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };

        var ctx = CreateContext(request, alexaContext, session: new AlexaSession { New = false });
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.NotNull(ctx.Response!.Response.Directives);
        Assert.Contains(directive, ctx.Response.Response.Directives);
    }

    [Fact]
    public async Task ProcessAsync_NewSession_InjectsDirective()
    {
        var userId = Guid.NewGuid();
        _config.Users.Add(new Jellyfin.Plugin.AlexaSkill.Entities.User { Id = userId });
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(directive);

        var interceptor = CreateInterceptor();
        var request = new IntentRequest { Type = "IntentRequest" };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };
        var session = new AlexaSession { New = true };

        var ctx = CreateContext(request, alexaContext, session);
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.NotNull(ctx.Response!.Response.Directives);
        Assert.Contains(directive, ctx.Response.Response.Directives);
    }

    [Fact]
    public async Task ProcessAsync_BuilderReturnsNull_DoesNotInjectDirective()
    {
        var userId = Guid.NewGuid();
        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns((DynamicEntitiesDirective?)null);

        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };

        var ctx = CreateContext(request, alexaContext);
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.True(ctx.Response!.Response.Directives == null || ctx.Response.Response.Directives.Count == 0);
    }

    [Fact]
    public async Task ProcessAsync_NoUserResolution_DoesNotCallBuilder()
    {
        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = "not-a-guid" },
                Device = new Device { DeviceID = "test-device" }
            }
        };

        var ctx = CreateContext(request, alexaContext);
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_PersonIdResolution_UsesConfigUser()
    {
        var testUserId = Guid.NewGuid();
        var personId = "amzn1.account.test123";
        var testUser = new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = testUserId,
            InvocationName = "test"
        };
        _config.AddUser(testUser);
        testUser.AlexaPersonId = personId;

        var directive = new DynamicEntitiesDirective();
        _builderMock
            .Setup(b => b.Build(testUserId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(directive);

        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = "not-used" },
                Device = new Device { DeviceID = "test-device" },
                Person = new Person { PersonId = personId }
            }
        };

        var ctx = CreateContext(request, alexaContext);
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(testUserId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_ResponseNull_DoesNotThrow()
    {
        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var ctx = CreateContext(request);
        ctx.Response = null;

        await interceptor.ProcessAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task ProcessAsync_ResponseResponseBodyNull_DoesNotThrow()
    {
        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var ctx = CreateContext(request);
        ctx.Response = new SkillResponse { Response = null };

        await interceptor.ProcessAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task ProcessAsync_BuilderException_SwallowsAndDoesNotInject()
    {
        var userId = Guid.NewGuid();
        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Throws(new InvalidOperationException("test failure"));

        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };

        var ctx = CreateContext(request, alexaContext);

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.True(ctx.Response!.Response.Directives == null || ctx.Response.Response.Directives.Count == 0);
    }

    [Fact]
    public async Task ProcessAsync_Cancellation_Propagates()
    {
        var userId = Guid.NewGuid();
        _config.Users.Add(new Jellyfin.Plugin.AlexaSkill.Entities.User { Id = userId });
        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Throws(new OperationCanceledException());

        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };

        var ctx = CreateContext(request, alexaContext);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => interceptor.ProcessAsync(ctx, CancellationToken.None));
    }

    public static IEnumerable<object[]> AudioPlayerSkipDirectives => new[]
    {
        new object[] { new AudioPlayerPlayDirective
        {
            AudioItem = new AudioItem
            {
                Stream = new AudioItemStream
                {
                    Url = "https://example.com/stream.mp3",
                    OffsetInMilliseconds = 0
                }
            }
        }},
        new object[] { new StopDirective() },
        new object[] { new ClearQueueDirective() },
    };

    [Theory]
    [MemberData(nameof(AudioPlayerSkipDirectives))]
    public async Task ProcessAsync_AudioPlayerDirective_SkipsDynamicEntities(IDirective directive)
    {
        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var ctx = CreateContext(request);
        ctx.Response.Response.Directives = new List<IDirective> { directive };

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);

        Assert.Single(ctx.Response.Response.Directives);
    }

    // Live bug 2026-08-21 (corr 9c796d4f, 07bf42a4): on a NEW session the interceptor
    // injected Dialog.UpdateDynamicEntities into a response that already carried a
    // Dialog.ElicitSlot (FindSong keyword elicitation). Amazon rejects the combination
    // with INVALID_RESPONSE "No other directives are allowed to be specified with a
    // Dialog directive", so opening the skill into a FindSong flow failed audibly.
    // Dialog.UpdateDynamicEntities must never ride along with another Dialog.* directive.
    [Fact]
    public async Task ProcessAsync_DialogElicitSlotInResponse_SkipsDynamicEntities()
    {
        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var ctx = CreateContext(request);
        ctx.Response.Response.Directives = new List<IDirective>
        {
            new ElicitSlotDirective("titleKeywords", "FindSongIntent")
        };

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);

        Assert.Single(ctx.Response.Response.Directives);
        Assert.DoesNotContain(ctx.Response.Response.Directives, d => d is DynamicEntitiesDirective);
    }

    [Fact]
    public async Task ProcessAsync_NoAudioPlayerDirective_NewSession_StillInjectsDynamicEntities()
    {
        var userId = Guid.NewGuid();
        _config.Users.Add(new Jellyfin.Plugin.AlexaSkill.Entities.User { Id = userId });
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(directive);

        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };

        var ctx = CreateContext(request, alexaContext);

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);

        Assert.NotNull(ctx.Response.Response.Directives);
        Assert.Contains(ctx.Response.Response.Directives, d => d is DynamicEntitiesDirective);
    }

    [Fact]
    public async Task ProcessAsync_TvIntentMidSession_InjectsWithSeries()
    {
        var userId = Guid.NewGuid();
        _config.Users.Add(new Jellyfin.Plugin.AlexaSkill.Entities.User { Id = userId });
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), true, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(directive);

        var interceptor = CreateInterceptor();
        var request = new IntentRequest
        {
            Type = "IntentRequest",
            Intent = new Intent { Name = "PlayEpisodeIntent" }
        };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };
        var session = new AlexaSession { New = false };

        var ctx = CreateContext(request, alexaContext, session);
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), true, false, It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);
        Assert.Contains(directive, ctx.Response!.Response.Directives!);
    }

    [Fact]
    public async Task ProcessAsync_BookIntentMidSession_InjectsWithAudiobooks()
    {
        var userId = Guid.NewGuid();
        _config.Users.Add(new Jellyfin.Plugin.AlexaSkill.Entities.User { Id = userId });
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, true, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(directive);

        var interceptor = CreateInterceptor();
        var request = new IntentRequest
        {
            Type = "IntentRequest",
            Intent = new Intent { Name = "GoToChapterIntent" }
        };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };
        var session = new AlexaSession { New = false };

        var ctx = CreateContext(request, alexaContext, session);
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), false, true, It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);
        Assert.Contains(directive, ctx.Response!.Response.Directives!);
    }

    [Fact]
    public async Task ProcessAsync_NonTvNonBookIntentMidSession_DoesNotInject()
    {
        var interceptor = CreateInterceptor();
        var request = new IntentRequest
        {
            Type = "IntentRequest",
            Intent = new Intent { Name = "PlayArtistSongsIntent" }
        };
        var session = new AlexaSession { New = false };
        var ctx = CreateContext(request, session: session);

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Theory]
    [InlineData("AMAZON.ShuffleOnIntent")]
    [InlineData("AMAZON.ShuffleOffIntent")]
    [InlineData("AMAZON.NextIntent")]
    [InlineData("AMAZON.PreviousIntent")]
    [InlineData("AMAZON.PauseIntent")]
    public async Task ProcessAsync_PlaybackControlIntent_NeverInjectsEvenOnNewSession(string intentName)
    {
        // Regression for issue #10 follow-up: ShuffleOn arrived on a fresh session
        // and the new-session path injected whole-library entities. Built-in
        // playback-control intents carry no slot to resolve, so they must always skip.
        var userId = Guid.NewGuid();
        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(new DynamicEntitiesDirective());

        var interceptor = CreateInterceptor();
        var request = new IntentRequest
        {
            Type = "IntentRequest",
            Intent = new Intent { Name = intentName }
        };
        var alexaContext = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };
        var session = new AlexaSession { New = true }; // would normally trigger injection

        var ctx = CreateContext(request, alexaContext, session);
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        _builderMock.Verify(
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Func<Guid[]?>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never);
    }
}

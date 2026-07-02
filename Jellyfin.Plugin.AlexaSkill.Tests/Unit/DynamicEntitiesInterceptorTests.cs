using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
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
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_LaunchRequest_InjectsDirective()
    {
        var userId = Guid.NewGuid();
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, false, It.IsAny<CancellationToken>()))
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
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, false, It.IsAny<CancellationToken>()))
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
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, false, It.IsAny<CancellationToken>()))
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
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
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
            .Setup(b => b.Build(testUserId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, false, It.IsAny<CancellationToken>()))
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
            b => b.Build(testUserId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, false, It.IsAny<CancellationToken>()),
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
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, false, It.IsAny<CancellationToken>()))
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
        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, false, It.IsAny<CancellationToken>()))
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
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.Single(ctx.Response.Response.Directives);
    }

    [Fact]
    public async Task ProcessAsync_NoAudioPlayerDirective_NewSession_StillInjectsDynamicEntities()
    {
        var userId = Guid.NewGuid();
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, false, It.IsAny<CancellationToken>()))
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
            b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, false, It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.NotNull(ctx.Response.Response.Directives);
        Assert.Contains(ctx.Response.Response.Directives, d => d is DynamicEntitiesDirective);
    }

    [Fact]
    public async Task ProcessAsync_TvIntentMidSession_InjectsWithSeries()
    {
        var userId = Guid.NewGuid();
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), true, false, It.IsAny<CancellationToken>()))
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
            b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), true, false, It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Contains(directive, ctx.Response!.Response.Directives!);
    }

    [Fact]
    public async Task ProcessAsync_BookIntentMidSession_InjectsWithAudiobooks()
    {
        var userId = Guid.NewGuid();
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, true, It.IsAny<CancellationToken>()))
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
            b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), false, true, It.IsAny<CancellationToken>()),
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
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
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
            .Setup(b => b.Build(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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
            b => b.Build(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

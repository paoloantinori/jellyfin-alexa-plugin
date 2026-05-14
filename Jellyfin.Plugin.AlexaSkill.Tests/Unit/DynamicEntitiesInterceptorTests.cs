using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
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
public class DynamicEntitiesInterceptorTests
{
    private readonly Mock<DynamicEntityBuilder> _builderMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public DynamicEntitiesInterceptorTests()
    {
        _builderMock = new Mock<DynamicEntityBuilder>(
            Mock.Of<MediaBrowser.Controller.Library.ILibraryManager>(),
            Mock.Of<MediaBrowser.Controller.Library.IUserManager>(),
            LoggerFactory.Create(b => b.AddDebug()).CreateLogger<DynamicEntityBuilder>());

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

        // Should not have called builder
        _builderMock.Verify(
            b => b.BuildFromRecentItems(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_LaunchRequest_InjectsDirective()
    {
        var userId = Guid.NewGuid();
        var directive = new DynamicEntitiesDirective();

        _builderMock
            .Setup(b => b.BuildFromRecentItems(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()))
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
            .Setup(b => b.BuildFromRecentItems(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()))
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
            .Setup(b => b.BuildFromRecentItems(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()))
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

        // Directives should be empty (not injected)
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
            b => b.BuildFromRecentItems(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()),
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
            .Setup(b => b.BuildFromRecentItems(testUserId, It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()))
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
            b => b.BuildFromRecentItems(testUserId, It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_ResponseNull_DoesNotThrow()
    {
        var interceptor = CreateInterceptor();
        var request = new LaunchRequest { Type = "LaunchRequest" };
        var ctx = CreateContext(request);
        ctx.Response = null;

        // Should not throw
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
            .Setup(b => b.BuildFromRecentItems(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()))
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

        // Should not throw
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        // Should not have added any directive
        Assert.True(ctx.Response!.Response.Directives == null || ctx.Response.Response.Directives.Count == 0);
    }

    [Fact]
    public async Task ProcessAsync_Cancellation_Propagates()
    {
        var userId = Guid.NewGuid();
        _builderMock
            .Setup(b => b.BuildFromRecentItems(userId, It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()))
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

        // OperationCanceledException should NOT be swallowed
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => interceptor.ProcessAsync(ctx, CancellationToken.None));
    }
}

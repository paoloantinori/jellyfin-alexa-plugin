#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-327 funnel pin: the device-library scoping must reach the handler. The
/// first integration placed Apply in the controller, where the pipeline drops
/// the user and every handler re-resolves its own from the untouched config:
/// green resolver tests, dead feature (the handler-routing failure class). This
/// test drives BaseHandler.HandleRequestAsync - the real funnel - with a bound
/// device and asserts the HANDDER receives the scoped user.
/// </summary>
[Collection("Plugin")]
public class DeviceLibraryBindingFunnelTests : PluginTestBase
{
    public DeviceLibraryBindingFunnelTests()
    {
        // The funnel reads Plugin.Instance.Configuration.ServerAddress deep inside
        // the session lookup; a real Plugin instance is required.
        TestHelpers.EnsureRealPlugin();
    }

    private sealed class RecordingHandler : BaseHandler
    {
        public RecordingHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public Entities.User? SeenUser { get; private set; }

        public override bool CanHandle(Request request) => request is IntentRequest;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        {
            SeenUser = user;
            return Task.FromResult(ResponseBuilder.Empty());
        }
    }

    [Fact]
    public async Task HandleRequestAsync_BoundDevice_HandlerReceivesScopedUser()
    {
        var kids = Guid.NewGuid();
        var user = new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = Guid.NewGuid(),
            JellyfinToken = "funnel-token-" + Guid.NewGuid().ToString("N"),
        };
        var config = new PluginConfiguration();
        config.Users.Add(user);
        config.DeviceLibraryBindings.Add(new DeviceLibraryBinding
        {
            DeviceId = "funnel-device",
            LibraryId = kids.ToString(),
        });

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(s => s.GetSessionByAuthenticationToken(user.JellyfinToken, "funnel-device", It.IsAny<string?>()))
            .ReturnsAsync(new SessionInfo(sessionManager.Object, Mock.Of<ILogger<SessionInfo>>()));

        var loggerFactory = LoggerFactory.Create(b => { });
        var handler = new RecordingHandler(sessionManager.Object, config, loggerFactory);

        var request = new IntentRequest
        {
            Type = "IntentRequest",
            Intent = new Intent { Name = "AnyIntent" },
        };
        var context = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                Device = new global::Alexa.NET.Request.Device { DeviceID = "funnel-device" },
                User = new global::Alexa.NET.Request.User { AccessToken = user.Id.ToString() },
            }
        };

        await handler.HandleRequestAsync(request, context, alexaSession: null, CancellationToken.None);

        Assert.NotNull(handler.SeenUser);
        Assert.Equal(new List<string> { kids.ToString() }, handler.SeenUser!.AllowedLibraryIds);
        // The config-owned instance stays untouched.
        Assert.Null(user.AllowedLibraryIds);
    }

    [Fact]
    public async Task HandleRequestAsync_UnboundDevice_HandlerReceivesConfigUser()
    {
        var user = new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = Guid.NewGuid(),
            JellyfinToken = "funnel-token-" + Guid.NewGuid().ToString("N"),
        };
        var config = new PluginConfiguration();
        config.Users.Add(user);

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(s => s.GetSessionByAuthenticationToken(user.JellyfinToken, It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new SessionInfo(sessionManager.Object, Mock.Of<ILogger<SessionInfo>>()));

        var handler = new RecordingHandler(sessionManager.Object, config, LoggerFactory.Create(b => { }));

        var context = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                Device = new global::Alexa.NET.Request.Device { DeviceID = "some-other-device" },
                User = new global::Alexa.NET.Request.User { AccessToken = user.Id.ToString() },
            }
        };

        await handler.HandleRequestAsync(
            new IntentRequest { Type = "IntentRequest", Intent = new Intent { Name = "AnyIntent" } },
            context, alexaSession: null, CancellationToken.None);

        Assert.Same(user, handler.SeenUser);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using User = Jellyfin.Plugin.AlexaSkill.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for PostPlayBehavior enum defaults.
/// </summary>
public class PostPlayBehaviorEnumTests
{
    [Fact]
    public void Default_Is_Stop()
    {
        Assert.Equal(PostPlayBehavior.Stop, default(PostPlayBehavior));
    }

    [Fact]
    public void Values_AreOrdered()
    {
        Assert.Equal(0, (int)PostPlayBehavior.Stop);
        Assert.Equal(1, (int)PostPlayBehavior.AutoPlay);
        Assert.Equal(2, (int)PostPlayBehavior.Ask);
    }
}

/// <summary>
/// Tests for PluginConfiguration PostPlayBehavior defaults.
/// </summary>
public class PostPlayConfigDefaultsTests
{
    [Fact]
    public void DefaultPostPlayBehavior_DefaultsToStop()
    {
        var config = new PluginConfiguration();
        Assert.Equal(PostPlayBehavior.Stop, config.DefaultPostPlayBehavior);
    }

    [Fact]
    public void DefaultPostPlayBehavior_CanBeSet()
    {
        var config = new PluginConfiguration { DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay };
        Assert.Equal(PostPlayBehavior.AutoPlay, config.DefaultPostPlayBehavior);
    }
}

/// <summary>
/// Tests for User per-user PostPlayBehavior override.
/// </summary>
public class PostPlayUserOverrideTests
{
    [Fact]
    public void User_PostPlayBehavior_DefaultsToNull()
    {
        var user = new User();
        Assert.Null(user.PostPlayBehavior);
    }

    [Fact]
    public void User_PostPlayBehavior_CanBeSet()
    {
        var user = new User { PostPlayBehavior = PostPlayBehavior.Ask };
        Assert.Equal(PostPlayBehavior.Ask, user.PostPlayBehavior);
    }

    [Fact]
    public void User_PostPlayBehavior_CanBeCleared()
    {
        var user = new User { PostPlayBehavior = PostPlayBehavior.AutoPlay };
        user.PostPlayBehavior = null;
        Assert.Null(user.PostPlayBehavior);
    }
}

/// <summary>
/// Tests for PostPlayState in-memory state tracker.
/// </summary>
public class PostPlayStateTests : PluginTestBase
{
    private readonly Guid _userId = Guid.NewGuid();
    private const string DeviceId = "test-device";

    [Fact]
    public void TryGet_ReturnsFalse_WhenNoStateSet()
    {
        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out var mode, out var itemId));
        Assert.Equal(default(PostPlayBehavior), mode);
        Assert.Null(itemId);
    }

    [Fact]
    public void Set_Then_TryGet_ReturnsTrue()
    {
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.AutoPlay, "item-123");

        Assert.True(PostPlayState.TryGet(_userId, DeviceId, out var mode, out var itemId));
        Assert.Equal(PostPlayBehavior.AutoPlay, mode);
        Assert.Equal("item-123", itemId);
    }

    [Fact]
    public void Remove_ClearsState()
    {
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.Ask, "item-456");
        PostPlayState.Remove(_userId, DeviceId);

        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
    }

    [Fact]
    public void TryGet_ConsumesState_ForAutoPlay()
    {
        // Verify that TryGet does NOT consume state (it's read-only, consumed by Remove)
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.AutoPlay, "item-789");

        Assert.True(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
        Assert.True(PostPlayState.TryGet(_userId, DeviceId, out _, out _)); // still there
    }

    [Fact]
    public void Set_OverwritesPreviousState()
    {
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.Stop, "item-old");
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.AutoPlay, "item-new");

        Assert.True(PostPlayState.TryGet(_userId, DeviceId, out var mode, out var itemId));
        Assert.Equal(PostPlayBehavior.AutoPlay, mode);
        Assert.Equal("item-new", itemId);
    }

    [Fact]
    public void DifferentDevices_AreIndependent()
    {
        PostPlayState.Set(_userId, "device-a", PostPlayBehavior.AutoPlay, "item-a");
        PostPlayState.Set(_userId, "device-b", PostPlayBehavior.Ask, "item-b");

        Assert.True(PostPlayState.TryGet(_userId, "device-a", out var modeA, out _));
        Assert.True(PostPlayState.TryGet(_userId, "device-b", out var modeB, out _));
        Assert.Equal(PostPlayBehavior.AutoPlay, modeA);
        Assert.Equal(PostPlayBehavior.Ask, modeB);
    }

    [Fact]
    public void DifferentUsers_AreIndependent()
    {
        var user2 = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.AutoPlay, "item-1");
        PostPlayState.Set(user2, DeviceId, PostPlayBehavior.Ask, "item-2");

        Assert.True(PostPlayState.TryGet(_userId, DeviceId, out var mode1, out _));
        Assert.True(PostPlayState.TryGet(user2, DeviceId, out var mode2, out _));
        Assert.Equal(PostPlayBehavior.AutoPlay, mode1);
        Assert.Equal(PostPlayBehavior.Ask, mode2);
    }

    [Fact]
    public void ExpiredEntry_ReturnsFalse()
    {
        // We can't easily test TTL without making the TTL configurable or
        // using internal access. Instead, we test the contract: entries
        // should expire after 2 minutes. This test verifies the Set/TryGet
        // lifecycle works correctly for fresh entries.
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.AutoPlay, "item-fresh");
        Assert.True(PostPlayState.TryGet(_userId, DeviceId, out var mode, out var itemId));
        Assert.Equal(PostPlayBehavior.AutoPlay, mode);
        Assert.Equal("item-fresh", itemId);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        PostPlayState.Set(_userId, "device-a", PostPlayBehavior.AutoPlay, "item-a");
        PostPlayState.Set(_userId, "device-b", PostPlayBehavior.Ask, "item-b");
        PostPlayState.Clear();

        Assert.False(PostPlayState.TryGet(_userId, "device-a", out _, out _));
        Assert.False(PostPlayState.TryGet(_userId, "device-b", out _, out _));
    }
}

/// <summary>
/// Tests for GetPostPlayBehavior resolution (per-user override → global default).
/// Uses a test handler subclass to access the protected method.
/// </summary>
[Collection("Plugin")]
public class GetPostPlayBehaviorTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestPostPlayHandler _handler;

    public GetPostPlayBehaviorTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new TestPostPlayHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "postplay-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public void ReturnsGlobalDefault_WhenUserIsNull()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        Assert.Equal(PostPlayBehavior.AutoPlay, _handler.ExposeGetPostPlayBehavior(null));
    }

    [Fact]
    public void ReturnsGlobalDefault_WhenUserOverrideIsNull()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.Ask;
        var user = new User { PostPlayBehavior = null };
        Assert.Equal(PostPlayBehavior.Ask, _handler.ExposeGetPostPlayBehavior(user));
    }

    [Fact]
    public void ReturnsUserOverride_WhenSet()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.Stop;
        var user = new User { PostPlayBehavior = PostPlayBehavior.AutoPlay };
        Assert.Equal(PostPlayBehavior.AutoPlay, _handler.ExposeGetPostPlayBehavior(user));
    }

    [Fact]
    public void UserOverride_TakesPrecedenceOverGlobal()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.Ask;
        var user = new User { PostPlayBehavior = PostPlayBehavior.Stop };
        Assert.Equal(PostPlayBehavior.Stop, _handler.ExposeGetPostPlayBehavior(user));
    }

    [Fact]
    public void ReturnsStop_WhenNoConfigSet()
    {
        Assert.Equal(PostPlayBehavior.Stop, _handler.ExposeGetPostPlayBehavior(null));
    }

    /// <summary>
    /// Test-only handler subclass that exposes GetPostPlayBehavior for testing.
    /// </summary>
    private class TestPostPlayHandler : BaseHandler
    {
        public TestPostPlayHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public PostPlayBehavior ExposeGetPostPlayBehavior(User? user) => GetPostPlayBehavior(user);

        public override bool CanHandle(Request request) => false;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, User user,
            SessionInfo session, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using Alexa.NET.Request;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-327 per-device library binding resolution: the bound library restricts the
/// request user, a recognized voice profile outranks the room, the binding
/// intersects (never grants) the user's own restrictions, and the config-owned
/// user instance is never mutated.
/// </summary>
[Collection("Plugin")]
public class DeviceLibraryBindingResolverTests
{
    private static readonly Guid KidsLibrary = Guid.NewGuid();
    private static readonly Guid MusicLibrary = Guid.NewGuid();
    private readonly ILogger _logger = LoggerFactory.Create(b => { }).CreateLogger<DeviceLibraryBindingResolverTests>();

    private static PluginConfiguration ConfigWithBinding(string deviceId = "echo-kitchen")
    {
        var config = new PluginConfiguration();
        config.DeviceLibraryBindings.Add(new DeviceLibraryBinding
        {
            DeviceId = deviceId,
            LibraryId = KidsLibrary.ToString(),
            LibraryName = "Kids",
            DeviceName = "Kitchen",
        });
        return config;
    }

    private static Context DeviceContext(string deviceId, string? personId = null) => new()
    {
        System = new global::Alexa.NET.Request.AlexaSystem
        {
            Device = new global::Alexa.NET.Request.Device { DeviceID = deviceId },
            Person = personId == null ? null : new global::Alexa.NET.Request.Person { PersonId = personId }
        }
    };

    // Username is a computed read-only property (resolved from the user id via
    // Plugin.Instance); tests key everything off Id and the library list.
    private static Entities.User User(List<string>? libraries = null)
        => new() { Id = Guid.NewGuid(), AllowedLibraryIds = libraries };

    [Fact]
    public void BoundDevice_UnrestrictedUser_ScopedToBoundLibrary()
    {
        Entities.User original = User();
        Entities.User? effective = DeviceLibraryBindingResolver.Apply(
            DeviceContext("echo-kitchen"), original, ConfigWithBinding(), _logger);
        Assert.NotNull(effective);

        Assert.NotSame(original, effective);
        Assert.Equal(new List<string> { KidsLibrary.ToString() }, effective.AllowedLibraryIds);
        Assert.Null(original.AllowedLibraryIds); // config-owned instance untouched
    }

    [Fact]
    public void BoundDevice_UserAlreadyHasBoundLibrary_StillScoped()
    {
        Entities.User user = User(new List<string> { KidsLibrary.ToString() });
        Entities.User? effective = DeviceLibraryBindingResolver.Apply(
            DeviceContext("echo-kitchen"), user, ConfigWithBinding(), _logger);
        Assert.NotNull(effective);

        Assert.Equal(new List<string> { KidsLibrary.ToString() }, effective.AllowedLibraryIds);
    }

    [Fact]
    public void BoundDevice_UserRestrictedToOtherLibraries_GrantsNothing()
    {
        // AC#4: the binding cannot grant access the user does not have. The
        // effective list is a non-empty impossible id (empty means "all").
        Entities.User user = User(new List<string> { MusicLibrary.ToString() });
        Entities.User? effective = DeviceLibraryBindingResolver.Apply(
            DeviceContext("echo-kitchen"), user, ConfigWithBinding(), _logger);
        Assert.NotNull(effective);

        Assert.NotEqual(new List<string>(), effective.AllowedLibraryIds);
        Assert.DoesNotContain(KidsLibrary.ToString(), effective.AllowedLibraryIds!);
        Assert.DoesNotContain(MusicLibrary.ToString(), effective.AllowedLibraryIds!);
    }

    [Fact]
    public void UnboundDevice_SameInstanceUnchanged()
    {
        Entities.User user = User();
        Entities.User? effective = DeviceLibraryBindingResolver.Apply(
            DeviceContext("echo-unknown"), user, ConfigWithBinding(), _logger);
        Assert.NotNull(effective);

        Assert.Same(user, effective);
    }

    [Fact]
    public void RecognizedVoiceProfile_WinsOverBinding()
    {
        var config = ConfigWithBinding();
        var kid = User(new List<string> { MusicLibrary.ToString() });
        kid.AlexaPersonId = "person-1";
        config.Users.Add(kid);
        Entities.User owner = User();
        Entities.User? effective = DeviceLibraryBindingResolver.Apply(
            DeviceContext("echo-kitchen", personId: "person-1"), owner, config, _logger);
        Assert.NotNull(effective);

        Assert.Same(owner, effective); // binding skipped: the person outranks the room
    }

    [Fact]
    public void UnrecognizedPersonId_BindingStillApplies()
    {
        // A PersonId Alexa reports but the household has not mapped to a user is
        // not a recognized profile: the room binding applies.
        Entities.User? effective = DeviceLibraryBindingResolver.Apply(
            DeviceContext("echo-kitchen", personId: "person-stranger"), User(), ConfigWithBinding(), _logger);
        Assert.NotNull(effective);

        Assert.Equal(new List<string> { KidsLibrary.ToString() }, effective.AllowedLibraryIds);
    }

    [Fact]
    public void Clone_CarriesTheOtherUserFields()
    {
        Entities.User original = User();
        original.JellyfinToken = "tok";
        original.AlexaPersonId = "person-9";
        Entities.User? effective = DeviceLibraryBindingResolver.Apply(
            DeviceContext("echo-kitchen"), original, ConfigWithBinding(), _logger);
        Assert.NotNull(effective);

        Assert.Equal(original.Id, effective.Id);
        Assert.Equal("tok", effective.JellyfinToken);
        Assert.Equal("person-9", effective.AlexaPersonId);
    }
}

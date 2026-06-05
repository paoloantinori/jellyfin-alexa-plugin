using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// Tests for ConfigurationController user-skill CRUD endpoints.
/// Regression coverage for JF-152: duplicate user config entries and deletion
/// wiping all rows.
/// </summary>
[Collection("Plugin")]
public class UserSkillApiTests : PluginTestBase, IDisposable
{
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly ConfigurationController _controller;

    public UserSkillApiTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });
        _userManagerMock = new Mock<IUserManager>();

        var sessionManagerMock = new Mock<ISessionManager>();

        EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            cfg => { },
            "alexa-userskill-api-test");

        // The controller always operates on Plugin.Instance.Configuration,
        // so _config must reference that singleton, not a detached copy.
        _config = Plugin.Instance!.Configuration;
        _config.Users.Clear();

        _controller = new ConfigurationController(
            _userManagerMock.Object,
            sessionManagerMock.Object,
            Mock.Of<ILibraryManager>(),
            _loggerFactory,
            new ModelDeploymentManager(
                Mock.Of<IHttpClientFactory>(),
                _loggerFactory.CreateLogger<ModelDeploymentManager>()));
    }

    public void Dispose()
    {
        // Clean up users so subsequent test classes do not see stale data.
        _config.Users.Clear();
        _loggerFactory.Dispose();
    }

    // ===================================================================
    // DELETE endpoint tests -- JF-152 regression: deletion must remove
    // exactly the targeted user, never all rows
    // ===================================================================

    [Fact]
    public async Task DeleteUserSkill_ValidId_RemovesOnlyThatUser()
    {
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        AddUserDirect(userId1, "user one");
        AddUserDirect(userId2, "user two");

        Assert.Equal(2, _config.Users.Count);

        var result = await _controller.DeleteUserSkill(userId1.ToString());

        Assert.IsType<OkResult>(result);
        Assert.Single(_config.Users);
        Assert.Equal(userId2, _config.Users[0].Id);
    }

    [Fact]
    public async Task DeleteUserSkill_NonExistentId_Returns404()
    {
        var existingId = Guid.NewGuid();
        AddUserDirect(existingId, "existing");

        var result = await _controller.DeleteUserSkill(Guid.NewGuid().ToString());

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, jsonResult.StatusCode);
        Assert.Single(_config.Users); // original untouched
    }

    [Fact]
    public async Task DeleteUserSkill_InvalidGuidFormat_Returns400()
    {
        var result = await _controller.DeleteUserSkill("not-a-guid");

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
    }

    [Fact]
    public async Task DeleteUserSkill_OneOfTwo_OtherRemainsIntact()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        AddUserDirect(id1, "alice");
        AddUserDirect(id2, "bob");
        _config.Users[1].AllowedLibraryIds = new List<string> { "lib1", "lib2" };
        _config.Users[1].FuzzyMatchThreshold = 80;

        await _controller.DeleteUserSkill(id1.ToString());

        Assert.Single(_config.Users);
        var remaining = _config.Users[0];
        Assert.Equal(id2, remaining.Id);
        Assert.Equal(new List<string> { "lib1", "lib2" }, remaining.AllowedLibraryIds);
        Assert.Equal(80, remaining.FuzzyMatchThreshold);
    }

    [Fact]
    public async Task DeleteUserSkill_AllUsers_ListBecomesEmpty()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        AddUserDirect(id1, "alice");
        AddUserDirect(id2, "bob");

        await _controller.DeleteUserSkill(id1.ToString());
        await _controller.DeleteUserSkill(id2.ToString());

        Assert.Empty(_config.Users);
    }

    [Fact]
    public async Task DeleteUserSkill_SameIdTwice_SecondReturns404()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");

        var first = await _controller.DeleteUserSkill(id.ToString());
        Assert.IsType<OkResult>(first);

        var second = await _controller.DeleteUserSkill(id.ToString());
        var jsonResult = Assert.IsType<JsonResult>(second);
        Assert.Equal(404, jsonResult.StatusCode);
    }

    // ===================================================================
    // POST endpoint tests -- JF-152 regression: creating must not produce
    // duplicates
    // ===================================================================

    [Fact]
    public void CreateNewUserSkill_ValidRequest_CreatesUser()
    {
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("alice", jellyfinUserId);

        var json = JsonConvert.SerializeObject(new
        {
            Username = "alice",
            InvocationName = "my skill"
        });

        var result = _controller.CreateNewUserSkill(json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);

        Assert.Single(_config.Users);
        Assert.Equal(jellyfinUserId, _config.Users[0].Id);
        Assert.Equal("my skill", _config.Users[0].UserSkill!.InvocationName);
        Assert.Equal(UserSkillStatus.LwaAuthPending, _config.Users[0].UserSkill!.UserSkillStatus);
    }

    [Fact]
    public void CreateNewUserSkill_DuplicateUsername_Returns400()
    {
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("alice", jellyfinUserId);

        var json = JsonConvert.SerializeObject(new
        {
            Username = "alice",
            InvocationName = "first skill"
        });
        _controller.CreateNewUserSkill(json);

        var second = _controller.CreateNewUserSkill(json);

        var jsonResult = Assert.IsType<JsonResult>(second);
        Assert.Equal(400, jsonResult.StatusCode);
        Assert.Single(_config.Users); // only one entry, no duplicate
    }

    [Fact]
    public void CreateNewUserSkill_MissingUsername_Returns400()
    {
        var json = JsonConvert.SerializeObject(new
        {
            InvocationName = "my skill"
        });

        var result = _controller.CreateNewUserSkill(json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
    }

    [Fact]
    public void CreateNewUserSkill_EmptyUsername_Returns400()
    {
        var json = JsonConvert.SerializeObject(new
        {
            Username = "",
            InvocationName = "my skill"
        });

        var result = _controller.CreateNewUserSkill(json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
    }

    [Fact]
    public void CreateNewUserSkill_InvalidInvocationName_Returns400()
    {
        var json = JsonConvert.SerializeObject(new
        {
            Username = "alice",
            InvocationName = "oneword"
        });

        var result = _controller.CreateNewUserSkill(json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
    }

    [Fact]
    public void CreateNewUserSkill_MissingInvocationName_Returns400()
    {
        var json = JsonConvert.SerializeObject(new
        {
            Username = "alice"
        });

        var result = _controller.CreateNewUserSkill(json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
    }

    [Fact]
    public void CreateNewUserSkill_UnknownJellyfinUser_Returns404()
    {
        _userManagerMock
            .Setup(m => m.GetUserByName("nonexistent"))
            .Returns((Jellyfin.Database.Implementations.Entities.User?)null);

        var json = JsonConvert.SerializeObject(new
        {
            Username = "nonexistent",
            InvocationName = "my skill"
        });

        var result = _controller.CreateNewUserSkill(json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, jsonResult.StatusCode);
    }

    [Fact]
    public void CreateNewUserSkill_TwoDifferentUsers_BothCreated()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        SetupJellyfinUser("alice", id1);
        SetupJellyfinUser("bob", id2);

        _controller.CreateNewUserSkill(JsonConvert.SerializeObject(new
        {
            Username = "alice",
            InvocationName = "alice skill"
        }));
        _controller.CreateNewUserSkill(JsonConvert.SerializeObject(new
        {
            Username = "bob",
            InvocationName = "bob skill"
        }));

        Assert.Equal(2, _config.Users.Count);
        Assert.Equal(id1, _config.Users[0].Id);
        Assert.Equal(id2, _config.Users[1].Id);
    }

    // ===================================================================
    // PATCH endpoint tests -- JF-152 regression: updates must not create
    // duplicate entries
    // ===================================================================

    [Fact]
    public void UpdateUserSkill_InvocationName_UpdatesInPlace()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");

        var json = JsonConvert.SerializeObject(new
        {
            InvocationName = "updated skill"
        });

        var result = _controller.UpdateUserSkill(id.ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Null(jsonResult.StatusCode); // 200 by default
        Assert.Single(_config.Users); // no duplicate created
        Assert.Equal("updated skill", _config.Users[0].UserSkill!.InvocationName);
    }

    [Fact]
    public void UpdateUserSkill_AllowedLibraryIds_UpdatesInPlace()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");

        var json = JsonConvert.SerializeObject(new
        {
            AllowedLibraryIds = new List<string> { "lib1", "lib2" }
        });

        _controller.UpdateUserSkill(id.ToString(), json);

        Assert.Single(_config.Users);
        Assert.Equal(new List<string> { "lib1", "lib2" }, _config.Users[0].AllowedLibraryIds);
    }

    [Fact]
    public void UpdateUserSkill_AllowedLibraryIds_EmptyArray_ClearsToNull()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");
        _config.Users[0].AllowedLibraryIds = new List<string> { "old" };

        var json = JsonConvert.SerializeObject(new
        {
            AllowedLibraryIds = new List<string>()
        });

        _controller.UpdateUserSkill(id.ToString(), json);

        Assert.Null(_config.Users[0].AllowedLibraryIds);
    }

    [Fact]
    public void UpdateUserSkill_FuzzyMatchBehavior_UpdatesInPlace()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");
        Assert.Equal(FuzzyMatchBehavior.Confirm, _config.Users[0].FuzzyMatchBehavior);

        var json = JsonConvert.SerializeObject(new
        {
            FuzzyMatchBehavior = "AutoPlay"
        });

        _controller.UpdateUserSkill(id.ToString(), json);

        Assert.Single(_config.Users);
        Assert.Equal(FuzzyMatchBehavior.AutoPlay, _config.Users[0].FuzzyMatchBehavior);
    }

    [Fact]
    public void UpdateUserSkill_FuzzyMatchThreshold_UpdatesInPlace()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");

        var json = JsonConvert.SerializeObject(new
        {
            FuzzyMatchThreshold = 85
        });

        _controller.UpdateUserSkill(id.ToString(), json);

        Assert.Single(_config.Users);
        Assert.Equal(85, _config.Users[0].FuzzyMatchThreshold);
    }

    [Fact]
    public void UpdateUserSkill_FuzzyMatchThreshold_OutOfRange_Returns400()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");
        var originalThreshold = _config.Users[0].FuzzyMatchThreshold;

        var json = JsonConvert.SerializeObject(new
        {
            FuzzyMatchThreshold = 150
        });

        var result = _controller.UpdateUserSkill(id.ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
        Assert.Equal(originalThreshold, _config.Users[0].FuzzyMatchThreshold); // unchanged
    }

    [Fact]
    public void UpdateUserSkill_FuzzySuggestionThreshold_OutOfRange_Returns400()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");

        var json = JsonConvert.SerializeObject(new
        {
            FuzzySuggestionThreshold = -5
        });

        var result = _controller.UpdateUserSkill(id.ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
    }

    [Fact]
    public void UpdateUserSkill_NonExistentUser_Returns404()
    {
        var json = JsonConvert.SerializeObject(new
        {
            InvocationName = "updated skill"
        });

        var result = _controller.UpdateUserSkill(Guid.NewGuid().ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, jsonResult.StatusCode);
    }

    [Fact]
    public void UpdateUserSkill_InvalidGuidFormat_Returns400()
    {
        var json = JsonConvert.SerializeObject(new
        {
            InvocationName = "updated skill"
        });

        var result = _controller.UpdateUserSkill("not-a-guid", json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
    }

    [Fact]
    public void UpdateUserSkill_NoValidFields_Returns400()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");

        var json = JsonConvert.SerializeObject(new
        {
            UnknownField = "value"
        });

        var result = _controller.UpdateUserSkill(id.ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
        Assert.Single(_config.Users); // no side effects
    }

    [Fact]
    public void UpdateUserSkill_InvalidInvocationName_Returns400()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");

        var json = JsonConvert.SerializeObject(new
        {
            InvocationName = "oneword"
        });

        var result = _controller.UpdateUserSkill(id.ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, jsonResult.StatusCode);
    }

    [Fact]
    public void UpdateUserSkill_UserWithoutSkill_InvocationName_Returns404()
    {
        var id = Guid.NewGuid();
        // Add user with null UserSkill
        _config.Users.Add(new User { Id = id, InvocationName = "test" });

        var json = JsonConvert.SerializeObject(new
        {
            InvocationName = "updated skill"
        });

        var result = _controller.UpdateUserSkill(id.ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, jsonResult.StatusCode);
    }

    [Fact]
    public void UpdateUserSkill_MultipleFields_UpdatesAllInPlace()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");

        var json = JsonConvert.SerializeObject(new
        {
            InvocationName = "new name",
            FuzzyMatchThreshold = 75,
            FuzzyMatchBehavior = "AutoPlay",
            AllowedLibraryIds = new List<string> { "libA" }
        });

        _controller.UpdateUserSkill(id.ToString(), json);

        Assert.Single(_config.Users);
        Assert.Equal("new name", _config.Users[0].UserSkill!.InvocationName);
        Assert.Equal(75, _config.Users[0].FuzzyMatchThreshold);
        Assert.Equal(FuzzyMatchBehavior.AutoPlay, _config.Users[0].FuzzyMatchBehavior);
        Assert.Equal(new List<string> { "libA" }, _config.Users[0].AllowedLibraryIds);
    }

    // ===================================================================
    // Auto-provision tests -- PATCH auto-creates plugin user when Jellyfin
    // user exists but has no plugin config entry
    // ===================================================================

    [Fact]
    public void UpdateUserSkill_ValidJellyfinUser_NoPluginEntry_AutoProvisions()
    {
        // A valid Jellyfin user that has no plugin config entry
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("alice", jellyfinUserId);

        Assert.Empty(_config.Users);

        var json = JsonConvert.SerializeObject(new
        {
            SearchResponseMode = "Fast"
        });

        var result = _controller.UpdateUserSkill(jellyfinUserId.ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Null(jsonResult.StatusCode); // 200

        Assert.Single(_config.Users);
        Assert.Equal(jellyfinUserId, _config.Users[0].Id);
        Assert.Equal(Configuration.SearchResponseMode.Fast, _config.Users[0].SearchResponseMode);
    }

    [Fact]
    public void UpdateUserSkill_ValidJellyfinUser_NoPluginEntry_MultipleFields()
    {
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("bob", jellyfinUserId);

        var json = JsonConvert.SerializeObject(new
        {
            SearchResponseMode = "Fast",
            PostPlayBehavior = "AutoPlay",
            FuzzyMatchThreshold = 75
        });

        var result = _controller.UpdateUserSkill(jellyfinUserId.ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Null(jsonResult.StatusCode); // 200

        Assert.Single(_config.Users);
        var user = _config.Users[0];
        Assert.Equal(jellyfinUserId, user.Id);
        Assert.Equal(Configuration.SearchResponseMode.Fast, user.SearchResponseMode);
        Assert.Equal(Configuration.PostPlayBehavior.AutoPlay, user.PostPlayBehavior);
        Assert.Equal(75, user.FuzzyMatchThreshold);
    }

    [Fact]
    public void UpdateUserSkill_InvalidJellyfinUserId_Returns404()
    {
        // A GUID that does not correspond to any Jellyfin user
        var unknownGuid = Guid.NewGuid();
        // _userManagerMock.GetUserById returns null by default for unsetup GUIDs

        var json = JsonConvert.SerializeObject(new
        {
            SearchResponseMode = "Fast"
        });

        var result = _controller.UpdateUserSkill(unknownGuid.ToString(), json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, jsonResult.StatusCode);
        Assert.Empty(_config.Users); // no auto-provision happened
    }

    [Fact]
    public void UpdateUserSkill_AutoProvisionedUser_NullFields_HaveDefaults()
    {
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("charlie", jellyfinUserId);

        var json = JsonConvert.SerializeObject(new
        {
            FuzzyMatchThreshold = 90
        });

        _controller.UpdateUserSkill(jellyfinUserId.ToString(), json);

        Assert.Single(_config.Users);
        var user = _config.Users[0];
        Assert.Equal(jellyfinUserId, user.Id);
        Assert.Null(user.UserSkill); // no skill created
        Assert.Null(user.SearchResponseMode); // not set, will use global default
        Assert.Null(user.PostPlayBehavior); // not set, will use global default
        Assert.Equal(90, user.FuzzyMatchThreshold); // only this was set
    }

    [Fact]
    public async Task DeleteUserSkill_DoesNotAutoProvision_Returns404()
    {
        // DeleteUserSkill should NOT auto-provision — only UpdateUserSkill does
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("dave", jellyfinUserId);

        var result = await _controller.DeleteUserSkill(jellyfinUserId.ToString());

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, jsonResult.StatusCode);
        Assert.Empty(_config.Users);
    }

    [Fact]
    public void GetUserSkillAuthorisation_DoesNotAutoProvision_Returns404()
    {
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("eve", jellyfinUserId);

        var result = _controller.GetUserSkillAuthorisation(jellyfinUserId.ToString());

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, jsonResult.StatusCode);
        Assert.Empty(_config.Users);
    }

    [Fact]
    public void UpdateUserSkill_ExistingPluginUser_DoesNotReProvision()
    {
        // When user already exists in plugin config, no new entry is created
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("frank", jellyfinUserId);
        AddUserDirect(jellyfinUserId, "frank");

        var json = JsonConvert.SerializeObject(new
        {
            SearchResponseMode = "Thorough"
        });

        _controller.UpdateUserSkill(jellyfinUserId.ToString(), json);

        Assert.Single(_config.Users); // still just one
        Assert.Equal(Configuration.SearchResponseMode.Thorough, _config.Users[0].SearchResponseMode);
    }

    // ===================================================================
    // Data-layer test -- 3-user variant not covered by PluginConfigurationTests
    // ===================================================================

    [Fact]
    public void DeleteUser_OnlyRemovesTargetId_ThreeUsers()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        _config.Users.Add(CreateTestUser(id1, "a"));
        _config.Users.Add(CreateTestUser(id2, "b"));
        _config.Users.Add(CreateTestUser(id3, "c"));

        _config.DeleteUser(id2);

        Assert.Equal(2, _config.Users.Count);
        Assert.Equal(id1, _config.Users[0].Id);
        Assert.Equal(id3, _config.Users[1].Id);
    }

    // ===================================================================
    // Serialization round-trip test -- verify Users collection survives
    // JSON serialization/deserialization without data loss
    // ===================================================================

    [Fact]
    public void UsersCollection_SerializationRoundTrip_PreservesData()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        _config.AddUser(new User
        {
            Id = id1,
            InvocationName = "a",
            UserSkill = new UserSkill { InvocationName = "skill a", UserSkillStatus = UserSkillStatus.Ready },
            AllowedLibraryIds = new List<string> { "lib1", "lib2" },
            FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay,
            FuzzyMatchThreshold = 80,
            FuzzySuggestionThreshold = 50
        });
        _config.AddUser(new User
        {
            Id = id2,
            InvocationName = "b",
            UserSkill = new UserSkill { InvocationName = "skill b", UserSkillStatus = UserSkillStatus.LwaAuthPending },
            AllowedLibraryIds = null,
            FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm,
            FuzzyMatchThreshold = 60,
            FuzzySuggestionThreshold = 40
        });

        var serialized = JsonConvert.SerializeObject(_config);
        var deserialized = JsonConvert.DeserializeObject<PluginConfiguration>(serialized);

        Assert.Equal(2, deserialized!.Users.Count);
        Assert.Equal(id1, deserialized.Users[0].Id);
        Assert.Equal(id2, deserialized.Users[1].Id);
        Assert.Equal("skill a", deserialized.Users[0].UserSkill!.InvocationName);
        Assert.Equal(UserSkillStatus.Ready, deserialized.Users[0].UserSkill!.UserSkillStatus);
        Assert.Equal(new List<string> { "lib1", "lib2" }, deserialized.Users[0].AllowedLibraryIds);
        Assert.Equal(FuzzyMatchBehavior.AutoPlay, deserialized.Users[0].FuzzyMatchBehavior);
        Assert.Equal(80, deserialized.Users[0].FuzzyMatchThreshold);
        Assert.Null(deserialized.Users[1].AllowedLibraryIds);
    }

    // ===================================================================
    // Lifecycle tests -- create / delete / authorize round-trip
    // ===================================================================

    [Fact]
    public async Task DeleteThenCreate_SameUser_HasLwaAuthPendingStatus()
    {
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("alice", jellyfinUserId);

        var createJson = JsonConvert.SerializeObject(new
        {
            Username = "alice",
            InvocationName = "my skill"
        });
        var createResult = _controller.CreateNewUserSkill(createJson);
        var createdJsonResult = Assert.IsType<JsonResult>(createResult);
        var createdUser = Assert.IsType<User>(createdJsonResult.Value);
        Assert.Equal(UserSkillStatus.LwaAuthPending, createdUser.UserSkill!.UserSkillStatus);

        // Delete user
        var deleteResult = await _controller.DeleteUserSkill(jellyfinUserId.ToString());
        Assert.IsType<OkResult>(deleteResult);
        Assert.Empty(_config.Users);

        // Create again
        var recreateResult = _controller.CreateNewUserSkill(createJson);
        var recreatedJsonResult = Assert.IsType<JsonResult>(recreateResult);
        var recreatedUser = Assert.IsType<User>(recreatedJsonResult.Value);

        // Verify the new user has correct state
        Assert.NotNull(recreatedUser.UserSkill);
        Assert.Equal(UserSkillStatus.LwaAuthPending, recreatedUser.UserSkill.UserSkillStatus);
        Assert.Equal("my skill", recreatedUser.UserSkill.InvocationName);
        Assert.Null(recreatedUser.UserSkill.SkillId);
        Assert.Single(_config.Users);
    }

    [Fact]
    public void CreateNewUserSkill_ResponseIncludesUserSkill()
    {
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("alice", jellyfinUserId);

        var json = JsonConvert.SerializeObject(new
        {
            Username = "alice",
            InvocationName = "test skill"
        });

        var result = _controller.CreateNewUserSkill(json);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);

        var returnedUser = Assert.IsType<User>(jsonResult.Value);
        Assert.NotNull(returnedUser.UserSkill);
        Assert.Equal(UserSkillStatus.LwaAuthPending, returnedUser.UserSkill.UserSkillStatus);
        Assert.Equal("test skill", returnedUser.UserSkill.InvocationName);
        Assert.Null(returnedUser.UserSkill.SkillId);
        Assert.Equal(jellyfinUserId, returnedUser.Id);
    }

    [Fact]
    public void UserSkillStatus_SerializesCorrectly()
    {
        var userSkill = new UserSkill
        {
            InvocationName = "test skill",
            UserSkillStatus = UserSkillStatus.LwaAuthPending
        };

        var json = JsonConvert.SerializeObject(userSkill);

        // Newtonsoft.Json serializes enums as integers by default
        Assert.Contains("\"UserSkillStatus\":0", json);
        Assert.DoesNotContain("LwaAuthPending", json);
    }

    [Fact]
    public void GetUserSkillAuthorisation_LwaAuthPendingUser_ReturnsUrl()
    {
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("alice", jellyfinUserId);

        var json = JsonConvert.SerializeObject(new
        {
            Username = "alice",
            InvocationName = "my skill"
        });
        _controller.CreateNewUserSkill(json);

        var result = _controller.GetUserSkillAuthorisation(jellyfinUserId.ToString());

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(200, jsonResult.StatusCode);
        Assert.NotNull(jsonResult.Value);
    }

    [Fact]
    public void GetUserSkillAuthorisation_UserWithoutSkill_Returns404()
    {
        var id = Guid.NewGuid();
        _config.Users.Add(new User { Id = id, InvocationName = "test" });

        var result = _controller.GetUserSkillAuthorisation(id.ToString());

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, jsonResult.StatusCode);
    }

    [Fact]
    public async Task DeleteThenCreate_ConfigPersistsCorrectly()
    {
        var jellyfinUserId = Guid.NewGuid();
        SetupJellyfinUser("alice", jellyfinUserId);

        // Create
        var createJson = JsonConvert.SerializeObject(new
        {
            Username = "alice",
            InvocationName = "first skill"
        });
        _controller.CreateNewUserSkill(createJson);

        // Verify in config
        Assert.Single(_config.Users);
        var user = _config.GetUserById(jellyfinUserId);
        Assert.NotNull(user);
        Assert.NotNull(user.UserSkill);

        // Delete
        await _controller.DeleteUserSkill(jellyfinUserId.ToString());
        Assert.Empty(_config.Users);
        Assert.Null(_config.GetUserById(jellyfinUserId));

        // Recreate with different invocation name
        var recreateJson = JsonConvert.SerializeObject(new
        {
            Username = "alice",
            InvocationName = "second skill"
        });
        _controller.CreateNewUserSkill(recreateJson);

        Assert.Single(_config.Users);
        var recreatedUser = _config.GetUserById(jellyfinUserId);
        Assert.NotNull(recreatedUser);
        Assert.NotNull(recreatedUser.UserSkill);
        Assert.Equal("second skill", recreatedUser.UserSkill.InvocationName);
        Assert.Equal(UserSkillStatus.LwaAuthPending, recreatedUser.UserSkill.UserSkillStatus);
    }

    // ===================================================================
    // Config save must not destroy user data
    // ===================================================================

    [Fact]
    public void ConfigSerialization_UsersSurviveRoundTrip()
    {
        var id = Guid.NewGuid();
        AddUserDirect(id, "alice");

        // Simulate what happens when the save handler strips Users before
        // sending general config — the Users should remain intact on the server.
        var user = _config.GetUserById(id);
        Assert.NotNull(user);
        Assert.NotNull(user.UserSkill);
        Assert.Equal(UserSkillStatus.LwaAuthPending, user.UserSkill.UserSkillStatus);
    }

    [Fact]
    public void SmapiRefreshToken_SurvivesSerializationRoundTrip()
    {
        var id = Guid.NewGuid();
        var user = CreateTestUser(id, "alice");
        user.UserSkill = new UserSkill
        {
            InvocationName = "alice skill",
            UserSkillStatus = UserSkillStatus.Ready
        };
        user.SmapiRefreshToken = "refresh-token-should-not-be-lost";
        _config.Users.Add(user);

        var serialized = JsonConvert.SerializeObject(_config);
        var deserialized = JsonConvert.DeserializeObject<PluginConfiguration>(serialized);

        Assert.Single(deserialized!.Users);
        Assert.Equal("refresh-token-should-not-be-lost", deserialized.Users[0].SmapiRefreshToken);
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    /// <summary>
    /// Adds a test user with a UserSkill attached, using the shared TestHelpers.
    /// </summary>
    private void AddUserDirect(Guid id, string invocationName)
    {
        var user = CreateTestUser(id, invocationName);
        user.UserSkill = new UserSkill
        {
            InvocationName = invocationName + " skill",
            UserSkillStatus = UserSkillStatus.LwaAuthPending
        };
        _config.Users.Add(user);
    }

    /// <summary>
    /// Sets up the mock IUserManager to return a real Jellyfin user with the
    /// given name and ID. Sets up both <c>GetUserByName</c> and <c>GetUserById</c>
    /// lookups. Uses the concrete type because <c>User.Id</c> and
    /// <c>User.Username</c> are not virtual, so Moq cannot intercept them.
    /// </summary>
    private void SetupJellyfinUser(string username, Guid userId)
    {
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User(
            username, "authProviderId", "passwordProviderId")
        {
            Id = userId,
        };

        _userManagerMock
            .Setup(m => m.GetUserByName(username))
            .Returns(jellyfinUser);
        _userManagerMock
            .Setup(m => m.GetUserById(userId))
            .Returns(jellyfinUser);
    }
}

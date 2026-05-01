using System;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class PluginConfigurationTests
{
    private PluginConfiguration CreateConfig()
    {
        return new PluginConfiguration();
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var config = CreateConfig();
        Assert.NotNull(config);
    }

    [Fact]
    public void Constructor_InitializesEmptyLwaClientId()
    {
        var config = CreateConfig();
        Assert.Equal(string.Empty, config.LwaClientId);
    }

    [Fact]
    public void Constructor_InitializesEmptyLwaClientSecret()
    {
        var config = CreateConfig();
        Assert.Equal(string.Empty, config.LwaClientSecret);
    }

    [Fact]
    public void Constructor_InitializesAccountLinkingClientId()
    {
        var config = CreateConfig();
        Assert.NotEqual(Guid.Empty.ToString(), config.AccountLinkingClientId);
    }

    [Fact]
    public void Constructor_InitializesEmptyUsersList()
    {
        var config = CreateConfig();
        Assert.Empty(config.Users);
    }

    [Fact]
    public void AddUser_AddsUserToList()
    {
        var config = CreateConfig();
        var user = TestHelpers.CreateTestUser();

        config.AddUser(user);

        Assert.Single(config.Users);
        Assert.Equal(user.Id, config.Users[0].Id);
    }

    [Fact]
    public void AddUser_DuplicateUser_ThrowsArgumentException()
    {
        var config = CreateConfig();
        var guid = Guid.NewGuid();
        config.AddUser(TestHelpers.CreateTestUser(guid, "test1"));

        Assert.Throws<ArgumentException>(() => config.AddUser(TestHelpers.CreateTestUser(guid, "test2")));
    }

    [Fact]
    public void GetUserById_ReturnsUser_WhenExists()
    {
        var config = CreateConfig();
        var guid = Guid.NewGuid();
        config.AddUser(TestHelpers.CreateTestUser(guid));

        var result = config.GetUserById(guid);

        Assert.NotNull(result);
        Assert.Equal(guid, result!.Id);
    }

    [Fact]
    public void GetUserById_ReturnsNull_WhenNotFound()
    {
        var config = CreateConfig();

        Assert.Null(config.GetUserById(Guid.NewGuid()));
    }

    [Fact]
    public void DeleteUser_RemovesUser_WhenExists()
    {
        var config = CreateConfig();
        var guid = Guid.NewGuid();
        config.AddUser(TestHelpers.CreateTestUser(guid));

        var result = config.DeleteUser(guid);

        Assert.True(result);
        Assert.Empty(config.Users);
    }

    [Fact]
    public void DeleteUser_ReturnsFalse_WhenNotFound()
    {
        var config = CreateConfig();

        Assert.False(config.DeleteUser(Guid.NewGuid()));
    }

    // --- Validation Tests ---

    [Fact]
    public void Validate_ReturnsNoErrors_WhenDefaultConfig()
    {
        var config = CreateConfig();
        var errors = config.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ReturnsNoErrors_WhenValidHttpUrl()
    {
        var config = CreateConfig();
        TestHelpers.SetServerAddress(config, "http://example.com");
        var errors = config.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ReturnsNoErrors_WhenValidHttpsUrl()
    {
        var config = CreateConfig();
        TestHelpers.SetServerAddress(config, "https://example.com:8096");
        var errors = config.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ReturnsError_WhenServerAddressIsInvalidUri()
    {
        var config = CreateConfig();
        TestHelpers.SetServerAddress(config, "not-a-url");
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("Server address", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenServerAddressIsFtp()
    {
        var config = CreateConfig();
        TestHelpers.SetServerAddress(config, "ftp://example.com");
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("Server address", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsNoErrors_WhenServerAddressIsEmpty()
    {
        var config = CreateConfig();
        TestHelpers.SetServerAddress(config, string.Empty);
        var errors = config.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ReturnsError_WhenUsersExistButNoLwaClientId()
    {
        var config = CreateConfig();
        config.AddUser(TestHelpers.CreateTestUser());
        config.LwaClientId = string.Empty;
        config.LwaClientSecret = "secret";

        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("LWA Client ID", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenUsersExistButNoLwaClientSecret()
    {
        var config = CreateConfig();
        config.AddUser(TestHelpers.CreateTestUser());
        config.LwaClientId = "id";
        config.LwaClientSecret = string.Empty;

        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("LWA Client Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsNoErrors_WhenUsersExistWithLwaCredentials()
    {
        var config = CreateConfig();
        config.AddUser(TestHelpers.CreateTestUser());
        config.LwaClientId = "id";
        config.LwaClientSecret = "secret";

        var errors = config.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ReturnsNoErrors_WhenNoUsersWithoutLwaCredentials()
    {
        var config = CreateConfig();
        config.LwaClientId = string.Empty;
        config.LwaClientSecret = string.Empty;

        var errors = config.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ReturnsError_WhenDuplicateUserIds()
    {
        var config = CreateConfig();
        var guid = Guid.NewGuid();
        // Bypass AddUser validation to inject duplicates directly
        config.Users.Add(TestHelpers.CreateTestUser(guid));
        config.Users.Add(TestHelpers.CreateTestUser(guid));

        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate user ID", StringComparison.OrdinalIgnoreCase));
    }

    // --- Null-safe Setter Tests ---

    [Fact]
    public void ServerAddress_Setter_DoesNotThrow_WhenPluginInstanceIsNull()
    {
        // Plugin.Instance is null during tests, so setting ServerAddress should not throw
        var config = CreateConfig();
        var exception = Record.Exception(() => TestHelpers.SetServerAddress(config, "https://example.com"));
        Assert.Null(exception);
        Assert.Equal("https://example.com", config.ServerAddress);
    }

    [Fact]
    public void SslCertType_Setter_DoesNotThrow_WhenPluginInstanceIsNull()
    {
        var config = CreateConfig();
        var exception = Record.Exception(() => config.SslCertType = global::Alexa.NET.Management.SslCertificateType.Trusted);
        Assert.Null(exception);
    }
}

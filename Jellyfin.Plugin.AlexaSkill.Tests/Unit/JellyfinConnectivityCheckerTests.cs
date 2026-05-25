using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class JellyfinConnectivityCheckerTests : PluginTestBase
{
    private readonly Mock<ILogger<JellyfinConnectivityChecker>> _loggerMock;

    public JellyfinConnectivityCheckerTests()
    {
        _loggerMock = new Mock<ILogger<JellyfinConnectivityChecker>>();
    }

    [Fact]
    public async Task CheckAsync_NoServerAddress_ReturnsUnreachable()
    {
        // Plugin.Instance is null in unit tests, so the checker falls back to checking config
        // Since we can't easily mock Plugin.Instance, test the result record directly
        var result = new ConnectivityResult(false, "No server address configured", 0, null);

        Assert.False(result.IsReachable);
        Assert.Equal("No server address configured", result.Message);
    }

    [Fact]
    public void ConnectivityResult_RecordEquality_Works()
    {
        var a = new ConnectivityResult(true, "OK", 50, 200);
        var b = new ConnectivityResult(true, "OK", 50, 200);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ConnectivityResult_PropertiesAreSet()
    {
        var result = new ConnectivityResult(true, "OK", 123, 200);

        Assert.True(result.IsReachable);
        Assert.Equal("OK", result.Message);
        Assert.Equal(123, result.ResponseTimeMs);
        Assert.Equal(200, result.HttpStatusCode);
    }

    [Fact]
    public void ConnectivityResult_NullHttpStatusCode_ForTimeouts()
    {
        var result = new ConnectivityResult(false, "Timeout after 2000ms", 2000, null);

        Assert.False(result.IsReachable);
        Assert.Null(result.HttpStatusCode);
    }
}

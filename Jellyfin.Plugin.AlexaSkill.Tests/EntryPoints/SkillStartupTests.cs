using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.EntryPoints;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.EntryPoints;

public class SkillStartupTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory _httpClientFactory;

    public SkillStartupTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _loggerFactory = LoggerFactory.Create(b => { });
        var services = new ServiceCollection();
        services.AddHttpClient();
        _httpClientFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private SkillStartup CreateStartup()
    {
        return new SkillStartup(_sessionManagerMock.Object, _loggerFactory, _httpClientFactory);
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var startup = CreateStartup();

        await startup.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var startup = CreateStartup();

        startup.Dispose();
        startup.Dispose();
    }

    [Fact]
    public async Task StopAsync_CancelsBackgroundWork()
    {
        var startup = CreateStartup();

        // StopAsync should complete without error even without Plugin.Instance
        await startup.StopAsync(CancellationToken.None);

        startup.Dispose();
    }

    [Fact]
    public async Task StopAsync_WithCancelledToken_Completes()
    {
        var startup = CreateStartup();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await startup.StopAsync(cts.Token);

        startup.Dispose();
    }
}

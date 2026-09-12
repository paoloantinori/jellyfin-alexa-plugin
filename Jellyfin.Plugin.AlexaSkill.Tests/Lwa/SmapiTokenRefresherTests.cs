#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Lwa;

/// <summary>
/// JF-545: SmapiTokenRefresher is the one refresh mechanism; its never-throws contract
/// is load-bearing (long-running callers use it inside their recovery path, where a
/// throw would abort the very operation the refresh was saving). These tests pin the
/// contract on the HTTP failure paths via the LwaClient test seam, plus the
/// mutate-and-persist happy path.
/// </summary>
[Collection("Plugin")]
public class SmapiTokenRefresherTests : IDisposable
{
    private static Entities.User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        InvocationName = "test",
        JellyfinToken = "test-token",
        SmapiRefreshToken = "refresh-token-1",
        SmapiDeviceToken = new DeviceToken("access-token-1", "refresh-token-1", "Bearer", 0),
    };

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated LWA endpoint failure");
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class HappyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"fresh-access\",\"refresh_token\":\"fresh-refresh\",\"token_type\":\"Bearer\",\"expires_in\":\"3600\"}",
                    Encoding.UTF8,
                    "application/json")
            });
    }

    public SmapiTokenRefresherTests()
    {
        // NullLoggerFactory.Instance: never disposed, unlike another suite's factory
        // that a shared Plugin.Instance may still reference when these tests run
        // (LwaClient.Logger reads Plugin.Instance.LoggerFactory per call).
        TestHelpers.EnsurePluginInstance(
            new Configuration.PluginConfiguration(),
            NullLoggerFactory.Instance,
            c => { },
            "smapi-token-refresher-tests");
    }

    /// <summary>
    /// The Plugin collection is shared and other suites replace/reset
    /// Plugin.Instance.Configuration between tests, so credentials are set per test,
    /// immediately before the act (the ctor-time value does not survive).
    /// </summary>
    private static void SetTestLwaCredentials()
    {
        Plugin.Instance!.Configuration.LwaClientId = "test-client-id";
        Plugin.Instance!.Configuration.LwaClientSecret = "test-client-secret";
    }

    private static void UseLiveLoggerFactory()
    {
        // Another suite's Plugin.Instance may carry a DISPOSED LoggerFactory, which
        // LwaClient.Logger reads per call; swap in one that is never disposed.
        Plugin.Instance!.SetLoggerFactoryForTests(NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        LwaClient.HttpClientOverrideForTests = null;
        // The Plugin collection is shared; do not leave our credentials and logger
        // factory on the instance for whichever suite runs next (JF-545 review).
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.LwaClientId = string.Empty;
            Plugin.Instance.Configuration.LwaClientSecret = string.Empty;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}" + (exception == null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message}"));
    }

    [Theory]
    [InlineData("throw")] // transport failure
    [InlineData("500")] // server error: LwaClient throws on non-success
    [InlineData("400")] // invalid_grant: LwaClient throws on non-success
    public async Task RefreshAsync_NeverThrows_OnHttpFailure(string mode)
    {
        LwaClient.HttpClientOverrideForTests = () => mode == "throw"
            ? new HttpClient(new ThrowingHandler())
            : new HttpClient(new StatusHandler(mode == "500" ? HttpStatusCode.InternalServerError : HttpStatusCode.BadRequest));

        var user = CreateUser();
        SetTestLwaCredentials();
        UseLiveLoggerFactory();

        // The whole point (JF-544/JF-545): this must return false, never throw, so a
        // recovery path cannot be aborted by the refresh itself.
        bool refreshed = await SmapiTokenRefresher.RefreshAsync(user, NullLogger.Instance);

        Assert.False(refreshed);
        Assert.Equal("access-token-1", user.SmapiDeviceToken!.AccessToken);
    }

    [Fact]
    public async Task RefreshAsync_HappyPath_RotatesAndPersists()
    {
        LwaClient.HttpClientOverrideForTests = () => new HttpClient(new HappyHandler());
        SetTestLwaCredentials();
        UseLiveLoggerFactory();

        var user = CreateUser();

        var logger = new RecordingLogger();
        bool refreshed = await SmapiTokenRefresher.RefreshAsync(user, logger);

        Assert.True(refreshed, string.Join("\n", logger.Lines));
        Assert.Equal("fresh-access", user.SmapiDeviceToken!.AccessToken);
        Assert.Equal("fresh-refresh", user.SmapiRefreshToken);
        Assert.True(SmapiTokenRefresher.RemainingLifetime(user) > TimeSpan.FromMinutes(50));
    }

    [Fact]
    public async Task RefreshAsync_NoLwaCredentials_ReturnsFalse()
    {
        SetTestLwaCredentials();
        Plugin.Instance!.Configuration.LwaClientId = string.Empty;
        try
        {
            var user = CreateUser();

            bool refreshed = await SmapiTokenRefresher.RefreshAsync(user, NullLogger.Instance);

            Assert.False(refreshed);
            Assert.Equal("access-token-1", user.SmapiDeviceToken!.AccessToken);
        }
        finally
        {
            Plugin.Instance!.Configuration.LwaClientId = "test-client-id";
        }
    }
}

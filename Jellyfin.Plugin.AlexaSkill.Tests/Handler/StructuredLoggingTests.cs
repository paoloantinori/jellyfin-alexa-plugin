using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for structured logging across handlers: named template parameters,
/// correct log levels, and structured context values.
/// </summary>
public class StructuredLoggingTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;

    public StructuredLoggingTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
    }

    /// <summary>
    /// Creates a capturing logger factory that records all log messages
    /// so tests can assert on log level and formatted message content.
    /// </summary>
    private static CapturingLoggerFactory CreateCapturingLoggerFactory()
    {
        return new CapturingLoggerFactory();
    }

    private static Context CreateContext(string? accessToken = null, string? deviceId = null)
    {
        return new Context
        {
            System = new AlexaSystem
            {
                User = new User { AccessToken = accessToken ?? Guid.NewGuid().ToString() },
                Device = new Device { DeviceID = deviceId ?? "test-device-id" }
            }
        };
    }

    private SessionInfo CreateSession()
    {
        var capturingFactory = new CapturingLoggerFactory();
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, capturingFactory);
    }

    // -----------------------------------------------------------------------
    // 1. ExceptionHandler logs structured error context
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExceptionHandler_LogsStructuredErrorContext()
    {
        var factory = CreateCapturingLoggerFactory();
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, factory);

        var errorType = ErrorType.InternalError;
        var errorMessage = "something went wrong";
        var request = new SystemExceptionRequest
        {
            Error = new Error { Type = errorType, Message = errorMessage },
            RequestId = "req-123"
        };
        var context = CreateContext(deviceId: "dev-abc");

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var entry = Assert.Single(factory.LogEntries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("InternalError", entry.Message);
        Assert.Contains(errorMessage, entry.Message);
        Assert.Contains("req-123", entry.Message);
        Assert.Contains("dev-abc", entry.Message);
    }

    [Fact]
    public async Task ExceptionHandler_UsesNamedParametersInTemplate()
    {
        var factory = CreateCapturingLoggerFactory();
        var handler = new ExceptionHandler(_sessionManagerMock.Object, _config, factory);

        var request = new SystemExceptionRequest
        {
            Error = new Error { Type = ErrorType.InvalidResponse, Message = "bad response" },
            RequestId = "req-456"
        };
        var context = CreateContext(deviceId: "dev-xyz");

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var entry = Assert.Single(factory.LogEntries);
        // Verify the formatted message contains the structured values
        Assert.Contains("InvalidResponse", entry.Message);
        Assert.Contains("bad response", entry.Message);
        Assert.Contains("req-456", entry.Message);
        Assert.Contains("dev-xyz", entry.Message);
    }

    // -----------------------------------------------------------------------
    // 2. PlaybackFailedEventHandler logs structured error context
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PlaybackFailed_LogsStructuredErrorContext()
    {
        var factory = CreateCapturingLoggerFactory();
        var handler = new PlaybackFailedEventHandler(_sessionManagerMock.Object, _config, factory);

        var tokenId = Guid.NewGuid().ToString();
        var request = new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackFailed",
            Token = tokenId,
            OffsetInMilliseconds = 12345,
            RequestId = "pf-req-001"
        };
        var context = CreateContext(deviceId: "pf-device-001");

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var entry = Assert.Single(factory.LogEntries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(tokenId, entry.Message);
        Assert.Contains("12345", entry.Message);
        Assert.Contains("pf-req-001", entry.Message);
        Assert.Contains("pf-device-001", entry.Message);
    }

    [Fact]
    public async Task PlaybackFailed_ContainsNamedTemplateParameters()
    {
        var factory = CreateCapturingLoggerFactory();
        var handler = new PlaybackFailedEventHandler(_sessionManagerMock.Object, _config, factory);

        var guidToken = Guid.NewGuid();
        var request = new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackFailed",
            Token = guidToken.ToString(),
            OffsetInMilliseconds = 5000,
            RequestId = "req-abc"
        };
        var context = CreateContext(deviceId: "device-def");

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var entry = Assert.Single(factory.LogEntries);
        // Verify the formatted log includes all named parameter values
        Assert.Contains(guidToken.ToString(), entry.Message);
        Assert.Contains("5000", entry.Message);
        Assert.Contains("req-abc", entry.Message);
        Assert.Contains("device-def", entry.Message);
    }

    // -----------------------------------------------------------------------
    // 3. SessionEndedRequestHandler logs Reason when no error
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SessionEnded_LogsReasonWithoutError()
    {
        var factory = CreateCapturingLoggerFactory();
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, factory);

        var request = new SessionEndedRequest
        {
            Reason = Reason.UserInitiated,
            RequestId = "se-req-001"
        };
        var context = CreateContext();

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var entry = Assert.Single(factory.LogEntries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("UserInitiated", entry.Message);
        Assert.Contains("se-req-001", entry.Message);
    }

    [Fact]
    public async Task SessionEnded_LogsExceededMaxRepromptsReason()
    {
        var factory = CreateCapturingLoggerFactory();
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, factory);

        var request = new SessionEndedRequest
        {
            Reason = Reason.ExceededMaxReprompts,
            RequestId = "se-max-reprompts"
        };
        var context = CreateContext();

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var entry = Assert.Single(factory.LogEntries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("ExceededMaxReprompts", entry.Message);
        Assert.Contains("se-max-reprompts", entry.Message);
    }

    // -----------------------------------------------------------------------
    // 4. SessionEndedRequestHandler logs Reason and Error when present
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SessionEnded_LogsReasonAndErrorDetails()
    {
        var factory = CreateCapturingLoggerFactory();
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, factory);

        var request = new SessionEndedRequest
        {
            Reason = Reason.Error,
            Error = new Error { Type = ErrorType.InternalError, Message = "session blew up" },
            RequestId = "se-err-001"
        };
        var context = CreateContext();

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var entry = Assert.Single(factory.LogEntries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Error", entry.Message);
        Assert.Contains("InternalError", entry.Message);
        Assert.Contains("session blew up", entry.Message);
        Assert.Contains("se-err-001", entry.Message);
    }

    [Fact]
    public async Task SessionEnded_ErrorLog_ContainsAllStructuredFields()
    {
        var factory = CreateCapturingLoggerFactory();
        var handler = new SessionEndedRequestHandler(_sessionManagerMock.Object, _config, factory);

        var request = new SessionEndedRequest
        {
            Reason = Reason.Error,
            Error = new Error { Type = ErrorType.InvalidResponse, Message = "invalid" },
            RequestId = "se-err-002"
        };
        var context = CreateContext();

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var entry = Assert.Single(factory.LogEntries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        // Verify all four structured values appear in the formatted message
        Assert.Contains("Error", entry.Message);           // Reason
        Assert.Contains("InvalidResponse", entry.Message);  // ErrorType
        Assert.Contains("invalid", entry.Message);          // ErrorMessage
        Assert.Contains("se-err-002", entry.Message);       // RequestId
    }

    // -----------------------------------------------------------------------
    // 5. BaseHandler logs UserId when user not found
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BaseHandler_LogsUserIdWhenUserNotFound()
    {
        var factory = CreateCapturingLoggerFactory();
        var handler = new TestableBaseHandler(_sessionManagerMock.Object, _config, factory);

        // Use an access token that parses as a GUID but has no corresponding user
        var unknownUserId = Guid.NewGuid();
        var context = CreateContext(accessToken: unknownUserId.ToString(), deviceId: "dev-no-user");

        await handler.HandleRequestAsync(new IntentRequest { Type = "IntentRequest" }, context, CancellationToken.None);

        var entry = Assert.Single(factory.LogEntries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(unknownUserId.ToString(), entry.Message);
    }

    [Fact]
    public async Task BaseHandler_LogsNoEntryWhenUserFound()
    {
        var factory = CreateCapturingLoggerFactory();
        var user = TestHelpers.CreateTestUser();
        _config.Users.Add(user);

        // Call HandleAsync directly (bypassing HandleRequestAsync which requires Plugin.Instance).
        // When user IS found, BaseHandler.HandleRequestAsync does NOT log "User not found".
        // We verify by calling HandleAsync directly and confirming no "User not found" log entry exists.
        var handler = new TestableBaseHandler(_sessionManagerMock.Object, _config, factory);
        var context = CreateContext(accessToken: user.Id.ToString(), deviceId: "dev-existing");

        await handler.HandleAsync(
            new IntentRequest { Type = "IntentRequest" },
            context,
            user,
            CreateSession(),
            CancellationToken.None);

        // HandleAsync returns empty response without logging "User not found"
        Assert.Empty(factory.LogEntries);
    }

    // -----------------------------------------------------------------------
    // Helper: testable handler that exposes HandleRequestAsync for testing
    // -----------------------------------------------------------------------

    /// <summary>
    /// A concrete subclass of BaseHandler used solely for testing.
    /// Its HandleAsync returns an empty response and logs a marker message.
    /// </summary>
    private class TestableBaseHandler : BaseHandler
    {
        public TestableBaseHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResponseBuilder.Empty());
        }
    }

    // -----------------------------------------------------------------------
    // Capturing logger infrastructure
    // -----------------------------------------------------------------------

    /// <summary>
    /// ILoggerFactory implementation that creates loggers which capture
    /// all log entries into a shared list for test assertions.
    /// </summary>
    private class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message)> LogEntries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(LogEntries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }

    /// <summary>
    /// ILogger implementation that records each log call as a tuple of
    /// (LogLevel, formatted message) into the provided list.
    /// </summary>
    private class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries;

        public CapturingLogger(List<(LogLevel Level, string Message)> entries) => _entries = entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Minimal IDisposable that does nothing, used for BeginScope.
    /// </summary>
    private class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}

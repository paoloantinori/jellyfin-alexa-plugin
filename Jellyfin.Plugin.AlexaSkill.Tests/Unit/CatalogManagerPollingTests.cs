#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for SMAPI updateRequest poll-status parsing and the catalog upload flow.
/// Covers JF-332: <see cref="CatalogManager"/>'s poll previously read status/version
/// from the JSON root, but SMAPI nests them under "lastUpdateRequest", so every poll
/// timed out and CatalogSync never populated AlbumName (album "jazz cafe" couldn't
/// route one-shot to PlayAlbumIntent).
/// </summary>
public class CatalogManagerPollingTests
{
    private static CatalogManager CreateManager(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeSmapiHandler(respond);
        var factory = new StubHttpClientFactory(() => new HttpClient(handler));
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<CatalogManager>();
        return new CatalogManager(factory, logger);
    }

    /// <summary>
    /// Builds a CatalogManager whose SMAPI flow accepts the version POST (202 + Location)
    /// then answers every poll GET with <paramref name="pollJson"/>.
    /// </summary>
    private static CatalogManager ManagerWithPollBody(string pollJson) => CreateManager(req =>
    {
        if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri == CatalogVersionEndpoint)
        {
            var r = new HttpResponseMessage(HttpStatusCode.Accepted);
            r.Headers.Location = new Uri(PollLocation);
            return r;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(pollJson, Encoding.UTF8, "application/json")
        };
    });

    private static CatalogPayload EmptyPayload => new() { Values = new List<CatalogValue>() };

    private const string CatalogVersionEndpoint =
        "https://api.amazonalexa.com/v1/skills/api/custom/interactionModel/catalogs/cat-1/versions";

    private const string PollLocation =
        "https://api.amazonalexa.com/v1/skills/api/custom/interactionModel/catalogs/cat-1/updateRequest/req-1";

    #region ExtractPollStatus — SMAPI nests status under "lastUpdateRequest"

    [Fact]
    public void ExtractPollStatus_CatalogSucceeded_ReturnsNestedStatusAndVersion()
    {
        // Official SMAPI GET .../updateRequest/{id} response shape.
        using var doc = JsonDocument.Parse("""{"lastUpdateRequest":{"status":"SUCCEEDED","version":"2"}}""");
        var (status, version, errors) = CatalogManager.ExtractPollStatus(doc);
        Assert.Equal("SUCCEEDED", status);
        Assert.Equal("2", version);
        Assert.Null(errors);
    }

    [Fact]
    public void ExtractPollStatus_InProgress_ReturnsPendingStatus_KeepsPolling()
    {
        using var doc = JsonDocument.Parse("""{"lastUpdateRequest":{"status":"IN_PROGRESS"}}""");
        var (status, version, errors) = CatalogManager.ExtractPollStatus(doc);
        Assert.Equal("IN_PROGRESS", status);
        Assert.Null(version);
        Assert.Null(errors);
    }

    [Fact]
    public void ExtractPollStatus_Failed_ReturnsErrors()
    {
        using var doc = JsonDocument.Parse("""{"lastUpdateRequest":{"status":"FAILED","errors":[{"message":"bad value"}]}}""");
        var (status, version, errors) = CatalogManager.ExtractPollStatus(doc);
        Assert.Equal("FAILED", status);
        Assert.Contains("\"message\":\"bad value\"", errors);
        Assert.Null(version);
    }

    [Fact]
    public void ExtractPollStatus_RootLevelStatus_FallbackWorks_ForNonCatalogEndpoints()
    {
        // Slot-type version endpoints or legacy shapes may put status at the root.
        using var doc = JsonDocument.Parse("""{"status":"SUCCEEDED","version":"1"}""");
        var (status, version, errors) = CatalogManager.ExtractPollStatus(doc);
        Assert.Equal("SUCCEEDED", status);
        Assert.Equal("1", version);
        Assert.Null(errors);
    }

    [Fact]
    public void ExtractPollStatus_EmptyLastUpdateRequest_ReturnsNulls()
    {
        using var doc = JsonDocument.Parse("""{"lastUpdateRequest":{}}""");
        var (status, version, errors) = CatalogManager.ExtractPollStatus(doc);
        Assert.Null(status);
        Assert.Null(version);
        Assert.Null(errors);
    }

    [Fact]
    public void ExtractPollStatus_NonObjectRoot_ReturnsNulls()
    {
        using var doc = JsonDocument.Parse("[]");
        var (status, version, errors) = CatalogManager.ExtractPollStatus(doc);
        Assert.Null(status);
        Assert.Null(version);
        Assert.Null(errors);
    }

    #endregion

    #region UploadCatalogValuesAsync — regression: no longer times out on nested status

    [Fact]
    public async Task UploadCatalogValuesAsync_NestedSucceeded_ReturnsVersion_WithoutTimeout()
    {
        // JF-332 regression: previously this threw TimeoutException because status
        // was read from the root (null) instead of lastUpdateRequest.
        var manager = ManagerWithPollBody("""{"lastUpdateRequest":{"status":"SUCCEEDED","version":"7"}}""");
        string version = await manager.UploadCatalogValuesAsync(
            "token", "cat-1", EmptyPayload, () => "https://example.com/cat", CancellationToken.None);
        Assert.Equal("7", version);
    }

    [Fact]
    public async Task UploadCatalogValuesAsync_NestedFailed_ThrowsWithErrors()
    {
        var manager = ManagerWithPollBody(
            """{"lastUpdateRequest":{"status":"FAILED","errors":[{"message":"rejected catalog"}]}}""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.UploadCatalogValuesAsync("token", "cat-1", EmptyPayload, () => "https://example.com/cat", CancellationToken.None));
        Assert.Contains("rejected catalog", ex.Message);
    }

    [Fact]
    public async Task UploadCatalogValuesAsync_RootLevelStatus_AlsoResolves()
    {
        // An endpoint returning root-level status (slot-type path) must still resolve.
        var manager = ManagerWithPollBody("""{"status":"SUCCEEDED","version":"3"}""");
        string version = await manager.UploadCatalogValuesAsync(
            "token", "cat-1", EmptyPayload, () => "https://example.com/cat", CancellationToken.None);
        Assert.Equal("3", version);
    }

    #endregion

    #region UploadCatalogValuesAsync — retry on transient GATEWAY_ERROR/503 (catalog source-URL fetch failure)

    /// <summary>
    /// Builds a manager whose first version attempt polls FAILED with a GATEWAY_ERROR/503
    /// (SMAPI could not fetch the source URL — e.g. reverse proxy still warming up after a
    /// Jellyfin restart), then succeeds on the retry. The URL factory must be called twice
    /// (each attempt needs a fresh source URL, because SMAPI consumes the URL on fetch).
    /// </summary>
    [Fact]
    public async Task UploadCatalogValuesAsync_TransientGatewayError_RetriesWithFreshUrlAndSucceeds()
    {
        int urlCalls = 0;
        int versionPosts = 0;
        var manager = CreateManager(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri == CatalogVersionEndpoint)
            {
                versionPosts++;
                // First version attempt polls FAILED (503 fetching source URL); retry succeeds.
                string pollBody = versionPosts == 1
                    ? """{"lastUpdateRequest":{"status":"FAILED","errors":[{"code":"GATEWAY_ERROR","message":"Upstream server returned an error: https://example.com/cat 503"}]}}"""
                    : """{"lastUpdateRequest":{"status":"SUCCEEDED","version":"8"}}""";
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri(PollLocation);
                // stash poll body for the GET responder via a header hack is messy; use a static field instead
                _currentPollBody = pollBody;
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_currentPollBody, Encoding.UTF8, "application/json")
            };
        });

        string version = await manager.UploadCatalogValuesAsync(
            "token",
            "cat-1",
            EmptyPayload,
            () => { urlCalls++; return $"https://example.com/cat-{urlCalls}"; },
            CancellationToken.None);

        Assert.Equal("8", version);
        Assert.True(urlCalls >= 2, $"URL factory should be called once per attempt (>=2 on retry), got {urlCalls}");
        Assert.True(versionPosts >= 2, $"Version POST should be sent once per attempt (>=2 on retry), got {versionPosts}");
    }

    /// <summary>
    /// A non-transient failure (e.g. "rejected catalog" — a real validation error, not a
    /// fetch/503) must NOT be retried; it should throw immediately so the error isn't masked.
    /// </summary>
    [Fact]
    public async Task UploadCatalogValuesAsync_NonTransientFailure_DoesNotRetry()
    {
        int versionPosts = 0;
        var manager = CreateManager(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri == CatalogVersionEndpoint)
            {
                versionPosts++;
                _currentPollBody = """{"lastUpdateRequest":{"status":"FAILED","errors":[{"message":"rejected catalog value"}]}}""";
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri(PollLocation);
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_currentPollBody, Encoding.UTF8, "application/json")
            };
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.UploadCatalogValuesAsync(
                "token",
                "cat-1",
                EmptyPayload,
                () => "https://example.com/cat",
                CancellationToken.None));
        Assert.Contains("rejected catalog value", ex.Message);
        Assert.Equal(1, versionPosts); // no retry
    }

    private static string _currentPollBody = """{"lastUpdateRequest":{"status":"SUCCEEDED","version":"1"}}""";

    #endregion
}

/// <summary>
/// HttpMessageHandler that routes every request through a test-supplied responder.
/// </summary>
internal sealed class FakeSmapiHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeSmapiHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_respond(request));
}

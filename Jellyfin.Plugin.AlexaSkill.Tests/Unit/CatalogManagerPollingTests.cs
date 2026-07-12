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
        var factory = new FuncHttpClientFactory(() => new HttpClient(handler));
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<CatalogManager>();
        return new CatalogManager(factory, logger);
    }

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
        var manager = CreateManager(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri == CatalogVersionEndpoint)
            {
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri(PollLocation);
                return r;
            }

            var poll = new HttpResponseMessage(HttpStatusCode.OK);
            poll.Content = new StringContent(
                """{"lastUpdateRequest":{"status":"SUCCEEDED","version":"7"}}""",
                Encoding.UTF8,
                "application/json");
            return poll;
        });

        var payload = new CatalogPayload { Values = new List<CatalogValue>() };
        string version = await manager.UploadCatalogValuesAsync(
            "token", "cat-1", payload, "https://example.com/cat", CancellationToken.None);

        Assert.Equal("7", version);
    }

    [Fact]
    public async Task UploadCatalogValuesAsync_NestedFailed_ThrowsWithErrors()
    {
        var manager = CreateManager(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri == CatalogVersionEndpoint)
            {
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri(PollLocation);
                return r;
            }

            var poll = new HttpResponseMessage(HttpStatusCode.OK);
            poll.Content = new StringContent(
                """{"lastUpdateRequest":{"status":"FAILED","errors":[{"message":"rejected catalog"}]}}""",
                Encoding.UTF8,
                "application/json");
            return poll;
        });

        var payload = new CatalogPayload { Values = new List<CatalogValue>() };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.UploadCatalogValuesAsync("token", "cat-1", payload, "https://example.com/cat", CancellationToken.None));
        Assert.Contains("rejected catalog", ex.Message);
    }

    [Fact]
    public async Task UploadCatalogValuesAsync_RootLevelStatus_AlsoResolves()
    {
        // An endpoint returning root-level status (slot-type path) must still resolve.
        var manager = CreateManager(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri == CatalogVersionEndpoint)
            {
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri(PollLocation);
                return r;
            }

            var poll = new HttpResponseMessage(HttpStatusCode.OK);
            poll.Content = new StringContent(
                """{"status":"SUCCEEDED","version":"3"}""",
                Encoding.UTF8,
                "application/json");
            return poll;
        });

        var payload = new CatalogPayload { Values = new List<CatalogValue>() };
        string version = await manager.UploadCatalogValuesAsync(
            "token", "cat-1", payload, "https://example.com/cat", CancellationToken.None);

        Assert.Equal("3", version);
    }

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

/// <summary>
/// IHttpClientFactory that always returns a client built from a test-supplied factory.
/// </summary>
internal sealed class FuncHttpClientFactory : IHttpClientFactory
{
    private readonly Func<HttpClient> _create;

    public FuncHttpClientFactory(Func<HttpClient> create) => _create = create;

    public HttpClient CreateClient(string name) => _create();
}

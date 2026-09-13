#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-552: the per-locale raw model PUT in SmapiManagement must preserve the
/// live model's catalog wiring (the typed Alexa.NET PUT cannot carry
/// valueCatalog). Composition test through the client seam: a wired live GET
/// must produce a wired PUT body; a missing live model (first deploy) must PUT
/// the plain embedded model.
/// </summary>
public class SmapiManagementWiringTests : IDisposable
{
    private readonly RecordingHandler _handler = new();

    public SmapiManagementWiringTests()
    {
        SmapiManagement.RawModelClientOverrideForTests = () => new HttpClient(_handler, disposeHandler: false);
    }

    public void Dispose()
    {
        SmapiManagement.RawModelClientOverrideForTests = null;
    }

    private static SmapiManagement CreateManager()
        => new(
            TestHelpers.CreateTestDeviceToken(accessToken: "test-access-token"),
            NullLoggerFactory.Instance);

    private static SkillInteractionModel BuildItItModel()
        => new("it-IT", TestLocales.ResourcePath("it-IT"), "mia collezione");

    [Fact]
    public async Task PutLocaleModel_LiveModelWired_PutBodyCarriesWiring()
    {
        _handler.GetResponseBody = """
            {"interactionModel":{"languageModel":{"invocationName":"mia collezione","intents":[],"types":[{"name":"SeriesName","valueSupplier":{"type":"CatalogValueSupplier","valueCatalog":{"catalogId":"amzn1.catalog.series-1","version":"42"}}}]}}}
            """;

        await CreateManager().PutLocaleModelPreservingWiringAsync("skill-1", "it-IT", BuildItItModel());

        Assert.NotNull(_handler.LastPutBody);
        Assert.Contains("amzn1.catalog.series-1", _handler.LastPutBody, StringComparison.Ordinal);
        Assert.Contains("valueSupplier", _handler.LastPutBody, StringComparison.Ordinal);
        Assert.Contains("Bearer test-access-token", _handler.LastPutAuthHeader, StringComparison.Ordinal);
        // The static SeriesName seed must be replaced, not kept alongside the catalog.
        using var doc = System.Text.Json.JsonDocument.Parse(_handler.LastPutBody!);
        var types = doc.RootElement.GetProperty("interactionModel").GetProperty("languageModel").GetProperty("types");
        var series = Assert.Single(types.EnumerateArray(), t => t.GetProperty("name").GetString() == "SeriesName");
        Assert.False(series.TryGetProperty("values", out _), "static seed values must not ride along with the grafted catalog type");
    }

    [Fact]
    public async Task PutLocaleModel_NoLiveModel_PutsPlainEmbeddedModel()
    {
        _handler.GetStatusCode = HttpStatusCode.NotFound;

        await CreateManager().PutLocaleModelPreservingWiringAsync("skill-1", "it-IT", BuildItItModel());

        Assert.NotNull(_handler.LastPutBody);
        Assert.DoesNotContain("valueSupplier", _handler.LastPutBody, StringComparison.Ordinal);
        // The embedded model's own static types are all there.
        Assert.Contains("SeriesName", _handler.LastPutBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutLocaleModel_UnsupportedLocale_SkipsTheGetEntirely()
    {
        // JF-543 + the efficiency pre-guard: ar-SA can never carry wiring, so the
        // wiring GET must not even fire.
        await CreateManager().PutLocaleModelPreservingWiringAsync("skill-1", "ar-SA", BuildItItModel());

        Assert.Equal(0, _handler.GetCount);
        Assert.NotNull(_handler.LastPutBody);
        Assert.DoesNotContain("valueSupplier", _handler.LastPutBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutLocaleModel_TransientGetFailure_ThrowsInsteadOfUnwiredPut()
    {
        // JF-555: only 404 means "no live model". A 429/5xx on the wiring GET must
        // throw (feeding the RetryHelper) rather than silently PUT unwired, which
        // would reintroduce the JF-552 regression until the next catalog sync.
        _handler.GetStatusCode = HttpStatusCode.InternalServerError;
        _handler.GetResponseBody = "{\"message\":\"slow down\"}";

        var ex = await Record.ExceptionAsync(() => CreateManager().PutLocaleModelPreservingWiringAsync("skill-1", "it-IT", BuildItItModel()));

        Assert.NotNull(ex);
        Assert.Contains("500", ex!.Message, StringComparison.Ordinal);
        Assert.Null(_handler.LastPutBody);
    }

    [Fact]
    public async Task PutLocaleModel_PutFails_ThrowsWithBody()
    {
        _handler.GetStatusCode = HttpStatusCode.NotFound;
        _handler.PutStatusCode = HttpStatusCode.BadRequest;
        _handler.PutResponseBody = "{\"message\":\"validation\"}";

        var ex = await Record.ExceptionAsync(() => CreateManager().PutLocaleModelPreservingWiringAsync("skill-1", "it-IT", BuildItItModel()));

        Assert.NotNull(ex);
        Assert.Contains("400", ex!.Message, StringComparison.Ordinal);
        Assert.Contains("validation", ex.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode GetStatusCode { get; set; } = HttpStatusCode.OK;

        public string? GetResponseBody { get; set; }

        public HttpStatusCode PutStatusCode { get; set; } = HttpStatusCode.OK;

        public string? PutResponseBody { get; set; }

        public string? LastPutBody { get; private set; }

        public string? LastPutAuthHeader { get; private set; }

        public int GetCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (request.Method == HttpMethod.Get)
            {
                GetCount++;
                return new HttpResponseMessage(GetStatusCode)
                {
                    Content = new StringContent(GetResponseBody ?? "{}", Encoding.UTF8, "application/json")
                };
            }

            LastPutBody = body;
            LastPutAuthHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(PutStatusCode)
            {
                Content = new StringContent(PutResponseBody ?? "{}", Encoding.UTF8, "application/json")
            };
        }
    }
}

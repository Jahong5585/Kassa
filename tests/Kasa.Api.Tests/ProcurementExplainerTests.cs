using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kasa.Api.Tests;

public sealed class ProcurementExplainerTests
{
    [Theory]
    [InlineData(200, """{"choices":[{"finish_reason":"stop","message":{"content":"Запасы заканчиваются."}}]}""", null)]
    [InlineData(200, """{"choices":[]}""", "procurement_ai_unavailable")]
    [InlineData(429, "{}", "procurement_ai_quota")]
    public async Task OpenRouterUsesFreeModelAndHandlesProviderResponses(int status, string body, string? errorCode)
    {
        using var handler = new TestHandler(status, body);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "OpenRouter",
            ["Ai:ProcurementEnabled"] = "true",
            ["OPENROUTER_API_KEY"] = "test-openrouter-key",
            ["GROQ_API_KEY"] = "different-key"
        }).Build();
        var service = new ProcurementExplainer(client, configuration, NullLogger<ProcurementExplainer>.Instance);
        var recommendation = new ReplenishmentRecommendation("test", DateTime.UtcNow, default, "UTC",
            Guid.NewGuid(), "private-product", "piece", "private-supplier", "private-sku",
            default, default, default, 1, 1, 0, 1, 0, 0, null, new("manual", true, null, 0, 0, []),
            [], null, null, 1, 1, null);

        if (errorCode is null)
            Assert.Equal("Запасы заканчиваются.", (await service.Explain(recommendation, "Russian", default)).Explanation);
        else
            Assert.Equal(errorCode, (await Assert.ThrowsAsync<PosError>(() =>
                service.Explain(recommendation, "Russian", default))).Message);

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", handler.Url);
        Assert.Equal("Bearer test-openrouter-key", handler.Authorization);
        Assert.DoesNotContain("private-", handler.Body);
        using var payload = JsonDocument.Parse(handler.Body);
        Assert.Equal("openrouter/free", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(320, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(payload.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    private sealed class TestHandler(int status, string body) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}

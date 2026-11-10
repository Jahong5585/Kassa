using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kasa.Api;

internal sealed class ProcurementExplainer(HttpClient client, IConfiguration configuration,
    ILogger<ProcurementExplainer> logger)
{
    private const string Model = "openai/gpt-oss-20b";

    public async Task<ReplenishmentExplanation> Explain(ReplenishmentRecommendation recommendation, string language,
        CancellationToken ct)
    {
        var key = configuration["GROQ_API_KEY"]?.Trim();
        if (!configuration.GetValue<bool>("Ai:ProcurementEnabled") || string.IsNullOrEmpty(key))
            throw new PosError(503, "procurement_ai_disabled");

        var projection = recommendation.Projection;
        // Only send fixed categories and flags. The actual quantities, dates, and identifiers stay on the POS server.
        var facts = JsonSerializer.Serialize(new
        {
            decision = recommendation.SuggestedQuantity switch
            {
                null => "unavailable",
                > 0 => "order_recommended",
                _ => "no_order_needed"
            },
            risk = projection?.Risk,
            noSellableStock = recommendation.SellableStock == 0,
            stockExcludedFromSale = recommendation.ExcludedStock > 0,
            dropsBelowSafetyStock = projection?.FirstBelowSafetyDate is not null,
            demandEstimatedManually = recommendation.Demand.Source == "manual",
            insufficientSalesHistory = recommendation.Demand.InsufficientHistory,
            supplierOfferMissing = recommendation.Quote is null,
            stockExpiresBeforeArrival = projection?.ExpiresBeforeArrival > 0,
            stockExpiresDuringCoverage = projection?.ExpiresDuringCoverage > 0,
            expectedDeliveryByArrival = recommendation.Deliveries.Any(delivery => delivery.Timing == "timely"),
            deliveryExpectedLater = recommendation.Deliveries.Any(delivery => delivery.Timing == "late"),
            packageRounding = projection is not null && recommendation.SuggestedQuantity > projection.UnroundedQuantity
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = Model,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = $"""
                            Explain the POS replenishment result in {language} in two short plain-text sentences.
                            Use only the supplied facts. The POS already calculated the decision.
                            Explain the main factor and mention uncertainty or shortage when present.
                            If the decision is unavailable, explain the missing data. If demand was estimated manually,
                            treat it as an estimate. Never give numbers, dates, prices, new quantities, or an order command.
                            """
                    },
                    new { role = "user", content = facts }
                },
                reasoning_effort = "low",
                include_reasoning = false,
                max_completion_tokens = 320
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new PosError(503, "procurement_ai_quota");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Procurement AI returned HTTP {StatusCode}", (int)response.StatusCode);
                throw new PosError(503, "procurement_ai_unavailable");
            }

            using var completion = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
            if (completion is null)
                throw new PosError(503, "procurement_ai_unavailable");
            var choice = completion.RootElement.GetProperty("choices").EnumerateArray().FirstOrDefault();
            var finishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null;
            var text = choice.TryGetProperty("message", out var message) &&
                       message.TryGetProperty("content", out var content)
                ? content.GetString()?.Trim()
                : null;
            if (finishReason != "stop" || string.IsNullOrEmpty(text) || text.Length > 700 ||
                text.Any(char.IsNumber))
                throw new PosError(503, "procurement_ai_unavailable");
            return new ReplenishmentExplanation(text, recommendation.CalculatedAt, recommendation.PolicyVersion,
                recommendation.OrderDate, recommendation.SuggestedQuantity);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PosError(503, "procurement_ai_timeout");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            logger.LogWarning("Procurement AI request failed ({ErrorType})", error.GetType().Name);
            throw new PosError(503, "procurement_ai_unavailable");
        }
    }
}

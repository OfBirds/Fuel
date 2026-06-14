using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Config;

namespace Api.Services;

/// <summary>
/// Generic OpenAI-compatible nutrition estimator (chat/completions, JSON mode, Bearer
/// auth). PARKED — not wired by default. Both shipping connections (DeepSeek text,
/// Claude vision) now go through <see cref="AnthropicEstimator"/> over the Anthropic
/// Messages API, since DeepSeek exposes an Anthropic-compatible endpoint too. This class
/// is kept ready for a future OpenAI-format provider (OpenAI, Azure OpenAI, vLLM, …):
/// register it in Program.cs and point it at the provider's base URL. Resilience is the
/// caller's typed-client pipeline; the CancellationToken threads through for cancel/timeout.
/// </summary>
public class OpenAiEstimator(HttpClient http, AiOptions options, ILogger<OpenAiEstimator> logger)
    : INutritionEstimator
{
    public bool SupportsImages => false; // text-only path; vision goes via AnthropicEstimator

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<NutritionEstimate> EstimateFromTextAsync(
        string description, IReadOnlyList<string> notes, CancellationToken ct)
        => CallAsync(BuildUserContent(description, notes), ct);

    public Task<NutritionEstimate> EstimateFromImageAsync(
        byte[] image, string contentType, IReadOnlyList<string> notes, CancellationToken ct)
        => throw new NotImplementedException(
            "OpenAiEstimator is text-only; route image estimation through AnthropicEstimator.");

    private async Task<NutritionEstimate> CallAsync(string userContent, CancellationToken ct)
    {
        // Timeout + retry are handled by the resilience pipeline; on exhaustion it
        // surfaces a transient HttpRequestException / TimeoutRejectedException, which the
        // controller maps to the manual fallback. A user cancel raises OperationCanceledException.
        var sw = Stopwatch.StartNew();
        using var resp = await SendAsync(userContent, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        var estimate = Parse(body);
        logger.LogInformation(
            "DeepSeek estimate ok: {Items} items, overall conf {Conf}, {Ms}ms, model {Model}",
            estimate.Items.Count, estimate.OverallConfidence, sw.ElapsedMilliseconds, options.Model);
        return estimate;
    }

    private async Task<HttpResponseMessage> SendAsync(string userContent, CancellationToken ct)
    {
        var reqBody = new
        {
            model = options.Model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userContent },
            },
            response_format = new { type = "json_object" },
            stream = false,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private NutritionEstimate Parse(string body)
    {
        var content = JsonSerializer.Deserialize<ChatResponse>(body, Json)?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new AiUnavailableException("Empty AI response.");

        EstimatePayload? payload;
        try { payload = JsonSerializer.Deserialize<EstimatePayload>(content, Json); }
        catch (JsonException) { throw new AiUnavailableException("Unparseable AI response."); }

        var items = (payload?.Items ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new EstimatedItem
            {
                Name = i.Name!.Trim(),
                Quantity = i.Quantity,
                Uom = string.IsNullOrWhiteSpace(i.Uom) ? "g" : i.Uom!.Trim(),
                Calories = i.Calories,
                Protein = i.Protein,
                Carbs = i.Carbs,
                Fat = i.Fat,
                Confidence = i.Confidence,
            })
            .ToList();

        if (items.Count == 0)
            throw new AiUnavailableException("AI returned no usable items.");

        return new NutritionEstimate { Items = items, OverallConfidence = payload!.OverallConfidence };
    }

    private static string BuildUserContent(string description, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Food description:");
        sb.AppendLine(description.Trim());
        var real = notes.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (real.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Clarifications from the user (most recent last):");
            foreach (var n in real)
                sb.AppendLine($"- {n.Trim()}");
        }
        return sb.ToString();
    }

    private const string SystemPrompt =
        "You are a nutrition estimator. Break a free-text food description into " +
        "individual items with estimated amounts and nutrition. A single description " +
        "may contain several foods.\n\n" +
        "CRITICAL RULE: You MUST use English (lowercase) for every food name, " +
        "regardless of the input language. This is a hard requirement — non-English " +
        "names will be rejected.\n\n" +
        "Respond with ONLY a JSON object of this exact shape (no prose):\n" +
        "{\"items\":[{\"name\":string,\"quantity\":number,\"uom\":string,\"calories\":number," +
        "\"protein\":number,\"carbs\":number,\"fat\":number,\"confidence\":number}]," +
        "\"overallConfidence\":number}\n" +
        "Rules: quantity is the eaten amount in the unit you choose (prefer g or ml; " +
        "use 'piece' for countable items); calories are kcal, macros in grams; " +
        "confidence and overallConfidence are 0..1.";

    // ── DeepSeek (OpenAI-compatible) response shapes ──
    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] Message? Message);
    private sealed record Message([property: JsonPropertyName("content")] string? Content);

    // ── The JSON the model is asked to return inside `content` ──
    private sealed record EstimatePayload(
        [property: JsonPropertyName("items")] List<ItemPayload>? Items,
        [property: JsonPropertyName("overallConfidence")] double OverallConfidence);

    private sealed record ItemPayload(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("quantity")] double Quantity,
        [property: JsonPropertyName("uom")] string? Uom,
        [property: JsonPropertyName("calories")] double Calories,
        [property: JsonPropertyName("protein")] double? Protein,
        [property: JsonPropertyName("carbs")] double? Carbs,
        [property: JsonPropertyName("fat")] double? Fat,
        [property: JsonPropertyName("confidence")] double Confidence);
}

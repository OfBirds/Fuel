using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Config;

namespace Api.Services;

/// <summary>
/// Claude nutrition estimator over the Anthropic Messages API (Anthropic.SDK not
/// used — the API is simple enough to call directly). Primary role is image/photo
/// estimation (Phase 3); text support is included for completeness and provider
/// comparison. Resilience (timeout + one retry) is on the typed client pipeline.
/// </summary>
public class ClaudeEstimator(HttpClient http, AiOptions options, ILogger<ClaudeEstimator> logger)
    : INutritionEstimator
{
    public bool SupportsImages => true;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<NutritionEstimate> EstimateFromTextAsync(
        string description, IReadOnlyList<string> notes, CancellationToken ct)
    {
        var userContent = new List<object> { BuildTextBlock(description, notes) };
        return await CallAsync(userContent, ct);
    }

    public async Task<NutritionEstimate> EstimateFromImageAsync(
        byte[] image, string contentType, IReadOnlyList<string> notes, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(image);
        var mediaType = string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType;
        var userContent = new List<object>
        {
            new
            {
                type = "image",
                source = new { type = "base64", media_type = mediaType, data = b64 },
            },
            BuildTextBlock("Estimate the food in this photo.", notes),
        };
        return await CallAsync(userContent, ct);
    }

    private async Task<NutritionEstimate> CallAsync(List<object> userContent, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var reqBody = new
        {
            model = options.ClaudeModel,
            max_tokens = 1024,
            system = SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = userContent },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", options.ClaudeApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        var estimate = Parse(body);
        logger.LogInformation(
            "Claude estimate ok: {Items} items, overall conf {Conf}, {Ms}ms, model {Model}",
            estimate.Items.Count, estimate.OverallConfidence, sw.ElapsedMilliseconds, options.ClaudeModel);
        return estimate;
    }

    private NutritionEstimate Parse(string body)
    {
        var msg = JsonSerializer.Deserialize<MessageResponse>(body, Json)
                  ?? throw new AiUnavailableException("Empty Claude response.");
        var content = msg.Content?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(content))
            throw new AiUnavailableException("Empty Claude response text.");

        // Claude sometimes wraps JSON in ``` fences — strip them.
        var json = content;
        if (json.StartsWith("```"))
            json = json.Split("\n", 2)[1..].Aggregate("", (a, b) => a + b).Split("\n```")[0];

        EstimatePayload? payload;
        try { payload = JsonSerializer.Deserialize<EstimatePayload>(json, Json); }
        catch (JsonException) { throw new AiUnavailableException("Unparseable Claude response."); }

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
            throw new AiUnavailableException("Claude returned no usable items.");

        return new NutritionEstimate { Items = items, OverallConfidence = payload!.OverallConfidence };
    }

    private static object BuildTextBlock(string description, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(description.Trim());
        var real = notes.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (real.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Clarifications (most recent last):");
            foreach (var n in real) sb.AppendLine($"- {n.Trim()}");
        }
        return new { type = "text", text = sb.ToString() };
    }

    private const string SystemPrompt =
        "You are a nutrition estimator. Break a food description or photo into " +
        "individual items with estimated amounts and nutrition.\n\n" +
        "CRITICAL RULE: You MUST use English (lowercase) for every food name, " +
        "regardless of the input language.\n\n" +
        "Respond with ONLY a JSON object of this exact shape (no prose):\n" +
        "{\"items\":[{\"name\":string,\"quantity\":number,\"uom\":string,\"calories\":number," +
        "\"protein\":number,\"carbs\":number,\"fat\":number,\"confidence\":number}]," +
        "\"overallConfidence\":number}\n" +
        "Rules: quantity is the eaten amount in the unit you choose (prefer g or ml; " +
        "use 'piece' for countable items); calories are kcal, macros in grams; " +
        "confidence and overallConfidence are 0..1.";

    // ── Anthropic response shapes ──
    private sealed record MessageResponse([property: JsonPropertyName("content")] List<ContentBlock>? Content);
    private sealed record ContentBlock([property: JsonPropertyName("text")] string? Text);

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

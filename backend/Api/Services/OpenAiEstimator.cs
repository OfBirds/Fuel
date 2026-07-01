using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Services;

/// <summary>
/// Nutrition estimator over the OpenAI-compatible chat/completions API (JSON mode, Bearer
/// auth). Drives any provider speaking that wire format — OpenAI, Azure OpenAI, vLLM,
/// SGLang, and notably Ollama for self-hosted local models. Vision uses the OpenAI
/// `image_url` (base64 data-URI) content part. Fully described by the injected
/// <see cref="ProviderConnection"/> (base URL + optional key + model); the key is omitted
/// for local servers that need none. The shared "ai" HttpClient carries the resilience
/// pipeline; requests use absolute URIs from the connection base. The CancellationToken
/// threads through for user-cancel/timeout.
/// </summary>
public class OpenAiEstimator(HttpClient http, ProviderConnection connection, ILogger<OpenAiEstimator> logger)
    : INutritionEstimator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<NutritionEstimate> EstimateFromTextAsync(
        string description, IReadOnlyList<string> notes, CancellationToken ct)
        => CallAsync(BuildText(description, notes), ct);

    public Task<NutritionEstimate> EstimateFromImageAsync(
        byte[] image, string contentType, string? description, IReadOnlyList<string> notes, CancellationToken ct)
    {
        var mediaType = string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType;
        var dataUri = $"data:{mediaType};base64,{Convert.ToBase64String(image)}";
        // OpenAI multimodal: user content is an array of parts (text + image_url).
        var content = new object[]
        {
            new { type = "text", text = BuildText(EstimatePrompts.ImageInstruction(description), notes) },
            new { type = "image_url", image_url = new { url = dataUri } },
        };
        return CallAsync(content, ct);
    }

    private async Task<NutritionEstimate> CallAsync(object userContent, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var reqBody = new
        {
            model = connection.Model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userContent },
            },
            response_format = new { type = "json_object" },
            stream = false,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{connection.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(connection.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        var estimate = Parse(body);
        logger.LogInformation(
            "OpenAI estimate ok: {Items} items, overall conf {Conf}, {Ms}ms, model {Model}",
            estimate.Items.Count, estimate.OverallConfidence, sw.ElapsedMilliseconds, connection.Model);
        return estimate;
    }

    private NutritionEstimate Parse(string body)
    {
        var content = JsonSerializer.Deserialize<ChatResponse>(body, Json)?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new AiUnavailableException("Empty AI response.");

        // Some local models fence the JSON despite json_object — strip ``` if present.
        var json = content.Trim();
        if (json.StartsWith("```"))
            json = json.Split("\n", 2)[1..].Aggregate("", (a, b) => a + b).Split("\n```")[0];

        EstimatePayload? payload;
        try { payload = JsonSerializer.Deserialize<EstimatePayload>(json, Json); }
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

    private static string BuildText(string description, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();
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
        "You are a nutrition estimator. Break a food description or photo into " +
        "individual items with estimated amounts and nutrition. A single input " +
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

    // ── OpenAI-compatible response shapes ──
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

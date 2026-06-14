using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Config;

namespace Api.Services;

/// <summary>
/// DeepSeek nutrition estimator over its OpenAI-compatible chat/completions API in
/// JSON mode. Best-effort external call (docs/ai-providers.md §Resilience):
/// time-boxed to AI_TIMEOUT_SECONDS with one retry on transient errors, threading the
/// caller's CancellationToken so a user "Cancel" — or the timeout — tears down the
/// in-flight request. A user cancel is never retried; malformed/odd JSON is a failure.
/// </summary>
public class DeepSeekEstimator(HttpClient http, AiOptions options, ILogger<DeepSeekEstimator> logger)
    : INutritionEstimator
{
    private const int MaxAttempts = 2; // initial + one retry

    public bool SupportsImages => true; // DeepSeek supports image input (wired in Phase 3)

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<NutritionEstimate> EstimateFromTextAsync(
        string description, IReadOnlyList<string> notes, CancellationToken ct)
        => CallAsync(BuildUserContent(description, notes), ct);

    public Task<NutritionEstimate> EstimateFromImageAsync(
        byte[] image, string contentType, IReadOnlyList<string> notes, CancellationToken ct)
        => throw new NotImplementedException("Photo estimation lands in Phase 3.");

    private async Task<NutritionEstimate> CallAsync(string userContent, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Per-call timeout linked to the caller's token: whichever trips first
            // (user Cancel via RequestAborted, or the timeout) cancels the request.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await SendAsync(userContent, timeoutCts.Token);
                if ((int)resp.StatusCode >= 500 && attempt < MaxAttempts)
                {
                    logger.LogWarning("DeepSeek {Status} (attempt {Attempt}) — retrying",
                        (int)resp.StatusCode, attempt);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                var estimate = Parse(body);
                logger.LogInformation(
                    "DeepSeek estimate ok: {Items} items, overall conf {Conf}, {Ms}ms, model {Model}",
                    estimate.Items.Count, estimate.OverallConfidence, sw.ElapsedMilliseconds, options.Model);
                return estimate;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller aborted (user Cancel / request abort) — propagate, never retry.
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                logger.LogWarning(ex, "DeepSeek transient failure (attempt {Attempt}) — retrying", attempt);
            }
            catch (AiUnavailableException)
            {
                throw; // already a clean failure (bad/empty JSON) — don't double-wrap
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DeepSeek estimate failed after {Attempt} attempt(s)", attempt);
                throw new AiUnavailableException("Estimation failed.");
            }
        }
    }

    // A timeout (not the caller's token) and network blips are transient → one retry.
    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or OperationCanceledException;

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
        "You are a nutrition estimator. Given a free-text description of food (and optional " +
        "clarifications), break it into individual food items and estimate each one's amount " +
        "and nutrition. A single description may contain several foods. Respond with ONLY a " +
        "JSON object of this exact shape:\n" +
        "{\"items\":[{\"name\":string,\"quantity\":number,\"uom\":string,\"calories\":number," +
        "\"protein\":number,\"carbs\":number,\"fat\":number,\"confidence\":number}]," +
        "\"overallConfidence\":number}\n" +
        "Rules: quantity is the eaten amount in the unit you choose (prefer g or ml; use 'piece' " +
        "for countable items); calories are kcal and protein/carbs/fat are grams for the stated " +
        "quantity; confidence and overallConfidence are 0..1. Do not include any prose.";

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

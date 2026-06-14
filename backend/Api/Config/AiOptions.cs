namespace Api.Config;

/// <summary>
/// AI nutrition-estimator settings, from flat AI_* env keys (same style as DB_*/SMTP_*).
/// The provider is an operator/deploy-time choice, never a user setting; the API key
/// stays in the env file, never in source. See docs/ai-providers.md.
/// </summary>
public class AiOptions
{
    // ── Primary (text) provider: DeepSeek ──
    public string Provider { get; set; } = "deepseek";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash";
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; } = 30;

    // ── Image provider: Claude (Haiku 4.5) ──
    public bool ClaudeEnabled { get; set; }
    public string ClaudeApiKey { get; set; } = "";
    public string ClaudeModel { get; set; } = "claude-haiku-4-5-20251001";
}

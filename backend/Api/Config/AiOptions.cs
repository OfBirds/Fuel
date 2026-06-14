namespace Api.Config;

/// <summary>
/// AI nutrition-estimator settings, from flat AI_* env keys (same style as DB_*/SMTP_*).
/// The provider is an operator/deploy-time choice, never a user setting; the API key
/// stays in the env file, never in source. See docs/ai-providers.md.
/// </summary>
public class AiOptions
{
    public string Provider { get; set; } = "deepseek";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-pro";
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

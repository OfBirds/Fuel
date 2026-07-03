namespace Api.Config;

/// <summary>
/// The AI provider registry — an ordered list of connectors, bound from the hot-reloadable
/// JSON config file (see docs/ai-providers.md). Each provider is pure data; secret key
/// VALUES never live here — a provider references a key by name (<see cref="AiProvider.KeyRef"/>)
/// and the backend resolves it from the AI_KEY_&lt;NAME&gt; env var at request time. This split
/// lets operators reorder / enable-disable / swap models / add a key-less or existing-key
/// provider with no redeploy (file edit → IOptionsMonitor reload); only a brand-new secret
/// needs a redeploy.
/// </summary>
public class AiProvidersOptions
{
    public List<AiProvider> Providers { get; set; } = [];
}

public class AiProvider
{
    /// <summary>Human label, used in logs and to disambiguate entries.</summary>
    public string Name { get; set; } = "";

    /// <summary>Wire format: "anthropic" (Messages API) or "openai" (chat/completions).</summary>
    public string Convention { get; set; } = "";

    /// <summary>What this model can do: "text" and/or "vision". Drives which chain it joins.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Endpoint base; the convention's path (/v1/messages or /chat/completions) is appended.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Model id sent to the provider.</summary>
    public string Model { get; set; } = "";

    /// <summary>Lower runs first within its capability chain.</summary>
    public int Order { get; set; }

    /// <summary>Off → skipped entirely (lets you disable without deleting). Default on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Name of the env key to use: KeyRef "claude" → AI_KEY_CLAUDE. Empty/omitted →
    /// no auth header (local servers like Ollama that need no key).</summary>
    public string? KeyRef { get; set; }

    public bool Has(string capability)
        => Capabilities.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase));
}

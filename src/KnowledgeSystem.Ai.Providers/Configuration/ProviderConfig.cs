using System.ComponentModel.DataAnnotations;

namespace KnowledgeSystem.Ai.Providers.Configuration;

public class ProviderConfig
{
    public string ProviderId { get; set; } = "default";

    public string Protocol { get; set; } = "mistral-chat-completions";

    [Required]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    public string? DefaultModel { get; set; }

    public int? NetworkTimeoutSeconds { get; set; }

    public Dictionary<string, string> Headers { get; set; } = [];

    public ModelCapabilities? Capabilities { get; set; }

    public string Key
    {
        get => ApiKey;
        set => ApiKey = value;
    }

    public string Model
    {
        get => DefaultModel ?? string.Empty;
        set => DefaultModel = value;
    }
}

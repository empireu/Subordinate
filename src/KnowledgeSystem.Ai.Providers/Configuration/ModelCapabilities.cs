namespace KnowledgeSystem.Ai.Providers.Configuration;

public sealed class ModelCapabilities
{
    public bool SupportsReasoning { get; set; }
    public bool SupportsImages { get; set; }
    public int? ContextWindow { get; set; }
    public int? MaxOutputTokens { get; set; }
}

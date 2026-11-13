namespace KnowledgeSystem.Ai.Providers.Abstractions;

public sealed class ChatRequestOptions
{
    public string? Model { get; init; }
    public float? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ToolChoice { get; init; }
    public string? PromptCacheKey { get; init; }
}

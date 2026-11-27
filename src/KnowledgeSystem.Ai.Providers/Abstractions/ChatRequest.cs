namespace KnowledgeSystem.Ai.Providers.Abstractions;

public sealed class ChatRequest
{
    public string? Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ChatTool>? Tools { get; init; }
    public string? ToolChoice { get; init; }
    public float? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? PromptCacheKey { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

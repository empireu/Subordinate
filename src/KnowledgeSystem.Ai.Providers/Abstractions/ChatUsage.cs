namespace KnowledgeSystem.Ai.Providers.Abstractions;

public sealed class ChatUsage
{
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? CachedInputTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int? TotalTokens { get; init; }
}

namespace KnowledgeSystem.Ai.Providers.Abstractions;

public sealed class ChatMessage
{
    public required ChatRole Role { get; init; }
    public required IReadOnlyList<ChatContent> Contents { get; init; }
    public string? AuthorName { get; init; }
    public string? MessageId { get; init; }
    public string? ResponseId { get; init; }
    public string? ModelId { get; init; }
}

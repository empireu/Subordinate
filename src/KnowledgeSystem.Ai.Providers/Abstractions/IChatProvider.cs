namespace KnowledgeSystem.Ai.Providers.Abstractions;

public interface IChatProvider
{
    string ProviderId { get; }

    string? DefaultModel { get; }

    IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        CancellationToken cancellationToken
    );

    Task<ChatMessage> CompleteAsync(
        ChatRequest request,
        CancellationToken cancellationToken
    );
}

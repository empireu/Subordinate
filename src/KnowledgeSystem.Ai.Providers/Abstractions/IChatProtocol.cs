namespace KnowledgeSystem.Ai.Providers.Abstractions;

public interface IChatProtocol
{
    string ProtocolId { get; }

    IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatProtocolRequest request,
        CancellationToken cancellationToken
    );
}

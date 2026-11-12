namespace KnowledgeSystem.Ai.Providers.Abstractions;

public sealed class ChatProtocolRequest
{
    public required Uri Endpoint { get; init; }
    public required string ApiKey { get; init; }
    public required ChatRequest Request { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public int? NetworkTimeoutSeconds { get; init; }
}

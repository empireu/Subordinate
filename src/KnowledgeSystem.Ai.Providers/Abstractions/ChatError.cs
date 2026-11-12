namespace KnowledgeSystem.Ai.Providers.Abstractions;

public sealed class ChatError
{
    public required string Message { get; init; }
    public string? Code { get; init; }
    public int? StatusCode { get; init; }
    public bool IsRetryable { get; init; }
    public string? RawBody { get; init; }
}

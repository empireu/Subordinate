namespace KnowledgeSystem.Ai.Providers.Abstractions;

public enum ChatFinishReason
{
    Unknown,
    Stop,
    Length,
    ToolCalls,
    ContentFilter,
    Error,
    Aborted
}

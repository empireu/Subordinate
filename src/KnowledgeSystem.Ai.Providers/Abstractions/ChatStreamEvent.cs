namespace KnowledgeSystem.Ai.Providers.Abstractions;

public abstract record ChatStreamEvent;

public sealed record StreamStartedEvent(
    string? ResponseId,
    string? ModelId
) : ChatStreamEvent;

public sealed record TextStartedEvent(
    int ContentIndex
) : ChatStreamEvent;

public sealed record TextDeltaEvent(
    int ContentIndex,
    string Delta
) : ChatStreamEvent;

public sealed record TextEndedEvent(
    int ContentIndex,
    string Text
) : ChatStreamEvent;

public sealed record ReasoningStartedEvent(
    int ContentIndex
) : ChatStreamEvent;

public sealed record ReasoningDeltaEvent(
    int ContentIndex,
    string Delta
) : ChatStreamEvent;

public sealed record ReasoningEndedEvent(
    int ContentIndex,
    string Text,
    string? Signature
) : ChatStreamEvent;

public sealed record ToolCallCompletedEvent(
    int ContentIndex,
    ToolCallContent ToolCall
) : ChatStreamEvent;

public sealed record UsageReceivedEvent(
    ChatUsage Usage
) : ChatStreamEvent;

public sealed record StreamCompletedEvent(
    ChatFinishReason FinishReason,
    ChatMessage Message
) : ChatStreamEvent;

public sealed record StreamErrorEvent(
    ChatError Error
) : ChatStreamEvent;

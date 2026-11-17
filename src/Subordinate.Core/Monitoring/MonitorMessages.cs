namespace Subordinate.Core.Monitoring;

public abstract record MonitorMessage;

public sealed record MonitorHello(
    string SessionDirectory,
    string Session,
    string Workdir,
    string RunId,
    string RunKind,
    int ProcessId,
    string ModelAlias,
    string ModelId,
    DateTimeOffset StartedAt) : MonitorMessage;

public sealed record MonitorSnapshot(
    string Phase,
    DateTimeOffset PhaseStartedAt,
    int ModelCall,
    string Reasoning,
    long ReasoningCharacters,
    string Text,
    long TextCharacters,
    IReadOnlyList<MonitorProposedCall> ProposedCalls,
    MonitorActiveTool? ActiveTool,
    MonitorStatusPayload? Status,
    int TranscriptCount,
    MonitorLastTurn? LastTurn) : MonitorMessage;

public sealed record MonitorTurnStarted(
    int ModelCall,
    DateTimeOffset At) : MonitorMessage;

public sealed record MonitorReasoningDelta(
    string Text) : MonitorMessage;

public sealed record MonitorTextDelta(
    string Text) : MonitorMessage;

public sealed record MonitorToolCallProposed(
    string CallId,
    string Tool,
    string Summary) : MonitorMessage;

public sealed record MonitorTurnCompleted(
    string FinishReason,
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    DateTimeOffset At) : MonitorMessage;

public sealed record MonitorTurnFailed(
    string Message) : MonitorMessage;

public sealed record MonitorToolStarted(
    string CallId,
    string Tool,
    string Summary,
    DateTimeOffset At) : MonitorMessage;

public sealed record MonitorToolFinished(
    string CallId,
    bool Ok,
    string FirstLine,
    DateTimeOffset At) : MonitorMessage;

public sealed record MonitorStatus(
    string State,
    double ElapsedSeconds,
    int TurnsRemaining,
    int PendingCount,
    string LastActivity,
    MonitorStatusCounters Run,
    MonitorStatusCounters SessionTotals,
    long ContextTokens) : MonitorMessage;

public sealed record MonitorTranscriptCommitted(
    int Count,
    long Bytes) : MonitorMessage;

public sealed record MonitorRunEnded(
    string State,
    string? ErrorMessage) : MonitorMessage;

public sealed record MonitorProposedCall(
    string CallId,
    string Tool,
    string Summary);

public sealed record MonitorActiveTool(
    string CallId,
    string Tool,
    string Summary,
    DateTimeOffset StartedAt);

public sealed record MonitorStatusCounters(
    int ModelCalls,
    int ToolCalls,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens);

public sealed record MonitorStatusPayload(
    string State,
    double ElapsedSeconds,
    int TurnsRemaining,
    int PendingCount,
    string LastActivity,
    MonitorStatusCounters Run,
    MonitorStatusCounters SessionTotals,
    long ContextTokens);

public sealed record MonitorLastTurn(
    double DurationSeconds,
    long? OutputTokens);

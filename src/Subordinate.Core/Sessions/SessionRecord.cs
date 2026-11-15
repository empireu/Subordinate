namespace Subordinate.Core.Sessions;

public static class SessionStates
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Finished = "finished";
    public const string AwaitingApproval = "awaiting_approval";
    public const string LimitReached = "limit_reached";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";

    public static bool IsActive(string state)
    {
        return state is Starting or Running;
    }
}

public static class PendingStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Closed = "closed";
}

public static class FrameStates
{
    public const string NotStarted = "not_started";
    public const string Started = "started";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Deferred = "deferred";
}

public sealed class ShellRecord
{
    public string Kind { get; set; } = string.Empty;

    public string Dialect { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;
}

public sealed class ModelRecord
{
    public string Alias { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;
}

public sealed class ErrorRecord
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

public sealed class TranscriptPointer
{
    public int Count { get; set; }

    public long Bytes { get; set; }
}

public sealed class InflightFrameRecord
{
    public string CallId { get; set; } = string.Empty;

    public string State { get; set; } = FrameStates.NotStarted;

    public string? Text { get; set; }
}

/// <summary>
///     A batch of tool calls whose results are not yet in the transcript.
/// </summary>
public sealed class InflightRecord
{
    public int AssistantIndex { get; set; }

    public List<InflightFrameRecord> Frames { get; set; } = [];
}

public sealed class GrantKeyRecord
{
    public string Key { get; set; } = string.Empty;

    public List<string> Capabilities { get; set; } = [];
}

public sealed class DecisionRecord
{
    public DateTimeOffset DecidedAt { get; set; }

    public string Scope { get; set; } = "once";

    public string? Reason { get; set; }
}

public sealed class ExecutionRecord
{
    public string State { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }

    public string? Output { get; set; }
}

public sealed class PendingRecord
{
    public string Id { get; set; } = string.Empty;

    public string CallId { get; set; } = string.Empty;

    public string Tool { get; set; } = string.Empty;

    public string RunId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string? Command { get; set; }

    public string? Shell { get; set; }

    public string? WorkingDirectory { get; set; }

    public string? Path { get; set; }

    public string ArgumentsJson { get; set; } = "{}";

    public string Fingerprint { get; set; } = string.Empty;

    public List<string> Capabilities { get; set; } = [];

    public List<string> Missing { get; set; } = [];

    public List<string> Reasons { get; set; } = [];

    public string ProposedScope { get; set; } = "once";

    public bool NeedsHuman { get; set; }

    public List<GrantKeyRecord> GrantKeys { get; set; } = [];

    public string DeferralMessage { get; set; } = string.Empty;

    public string Status { get; set; } = PendingStatuses.Pending;

    public DecisionRecord? Decision { get; set; }

    public ExecutionRecord? Execution { get; set; }

    /// <summary>
    ///     True once the decision or its execution output has been reported to the model.
    /// </summary>
    public bool Delivered { get; set; }
}

public sealed class GrantRecord
{
    public string PendingId { get; set; } = string.Empty;

    public string Tool { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public List<string> Capabilities { get; set; } = [];
}

public sealed class RunRecord
{
    public string RunId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public string State { get; set; } = string.Empty;

    public CountersRecord Counters { get; set; } = new();

    public bool FinalTurnForced { get; set; }
}

public sealed class CountersRecord
{
    public int ModelCalls { get; set; }

    public int ToolCalls { get; set; }

    public long InputTokens { get; set; }

    public long CachedInputTokens { get; set; }

    public long OutputTokens { get; set; }
}

public sealed class LimitsRecord
{
    public int MaxTurns { get; set; }

    public int MaxMinutes { get; set; }

    public int ShellTimeoutSeconds { get; set; }
}

/// <summary>
///     The persisted state of a session, stored as session.json. The conversation itself is in transcript.jsonl.
/// </summary>
public sealed class SessionRecord
{
    public int Schema { get; set; } = 1;

    public string Name { get; set; } = string.Empty;

    public string Workdir { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ShellRecord Shell { get; set; } = new();

    public ModelRecord Model { get; set; } = new();

    public List<string> Policy { get; set; } = [];

    public LimitsRecord Limits { get; set; } = new();

    public string State { get; set; } = SessionStates.Starting;

    public ErrorRecord? Error { get; set; }

    public TranscriptPointer Transcript { get; set; } = new();

    public InflightRecord? Inflight { get; set; }

    public List<PendingRecord> Pending { get; set; } = [];

    public List<GrantRecord> Grants { get; set; } = [];

    public List<RunRecord> Runs { get; set; } = [];

    public string? QueuedMessage { get; set; }

    public List<string> FilesWritten { get; set; } = [];

    public CountersRecord Counters { get; set; } = new();

    public string? FinalText { get; set; }

    public int PendingCount => Pending.Count(item => item.Status == PendingStatuses.Pending);

    public RunRecord? CurrentRun => Runs.Count > 0 ? Runs[^1] : null;
}

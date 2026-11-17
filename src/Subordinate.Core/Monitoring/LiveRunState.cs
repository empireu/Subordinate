using System.Text;

namespace Subordinate.Core.Monitoring;

public sealed class LiveRunState : IEquatable<LiveRunState>
{
    private readonly StringBuilder _reasoning = new();
    private readonly StringBuilder _text = new();
    private readonly List<MonitorProposedCall> _proposedCalls = [];

    public string? SessionDirectory { get; private set; }

    public string? Session { get; private set; }

    public string? Workdir { get; private set; }

    public string? RunId { get; private set; }

    public string? RunKind { get; private set; }

    public int ProcessId { get; private set; }

    public string? ModelAlias { get; private set; }

    public string? ModelId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public string Phase { get; private set; } = "starting";

    public DateTimeOffset PhaseStartedAt { get; private set; } = DateTimeOffset.MinValue;

    public int ModelCall { get; private set; }

    public string Reasoning => Tail(_reasoning, MonitorProtocol.LiveTextTailCharacters);

    public long ReasoningCharacters { get; private set; }

    public string Text => Tail(_text, MonitorProtocol.LiveTextTailCharacters);

    public long TextCharacters { get; private set; }

    public IReadOnlyList<MonitorProposedCall> ProposedCalls => _proposedCalls;

    public MonitorActiveTool? ActiveTool { get; private set; }

    public MonitorStatusPayload? Status { get; private set; }

    public int TranscriptCount { get; private set; }

    public MonitorLastTurn? LastTurn { get; private set; }

    public DateTimeOffset? TurnStartedAt { get; private set; }

    public string? LastFinishedTool { get; private set; }

    public string? LastToolFirstLine { get; private set; }

    public string? LastTurnError { get; private set; }

    public string? EndedState { get; private set; }

    public string? ErrorMessage { get; private set; }

    public void Apply(MonitorMessage message)
    {
        switch (message)
        {
            case MonitorHello hello:
            {
                SessionDirectory = hello.SessionDirectory;
                Session = hello.Session;
                Workdir = hello.Workdir;
                RunId = hello.RunId;
                RunKind = hello.RunKind;
                ProcessId = hello.ProcessId;
                ModelAlias = hello.ModelAlias;
                ModelId = hello.ModelId;
                StartedAt = WireTime(hello.StartedAt);
                Phase = "starting";
                PhaseStartedAt = StartedAt;
                break;
            }
            case MonitorSnapshot snapshot:
            {
                ApplySnapshot(snapshot);
                break;
            }
            case MonitorTurnStarted turnStarted:
            {
                _reasoning.Clear();
                _text.Clear();
                _proposedCalls.Clear();
                ModelCall = turnStarted.ModelCall;
                Phase = "waiting_for_model";
                PhaseStartedAt = WireTime(turnStarted.At);
                TurnStartedAt = turnStarted.At;
                break;
            }
            case MonitorReasoningDelta reasoningDelta:
            {
                AppendTail(_reasoning, reasoningDelta.Text);
                ReasoningCharacters += reasoningDelta.Text.Length;
                SetPhase("reasoning");
                break;
            }
            case MonitorTextDelta textDelta:
            {
                AppendTail(_text, textDelta.Text);
                TextCharacters += textDelta.Text.Length;
                SetPhase("answering");
                break;
            }
            case MonitorToolCallProposed toolCallProposed:
            {
                if (_proposedCalls.Count < MonitorProtocol.MaxProposedCalls)
                {
                    _proposedCalls.Add(new MonitorProposedCall(toolCallProposed.CallId, toolCallProposed.Tool, toolCallProposed.Summary));
                }

                break;
            }
            case MonitorTurnCompleted turnCompleted:
            {
                var duration = TurnStartedAt.HasValue ? Math.Max(0, (turnCompleted.At - TurnStartedAt.Value).TotalSeconds) : 0;
                LastTurn = new MonitorLastTurn(duration, turnCompleted.OutputTokens);
                SetPhase("turn_done", turnCompleted.At);
                break;
            }
            case MonitorTurnFailed turnFailed:
            {
                LastTurnError = turnFailed.Message;
                SetPhase("turn_done");
                break;
            }
            case MonitorToolStarted toolStarted:
            {
                ActiveTool = new MonitorActiveTool(toolStarted.CallId, toolStarted.Tool, toolStarted.Summary, toolStarted.At);
                SetPhase("running_tools", toolStarted.At);
                break;
            }
            case MonitorToolFinished toolFinished:
            {
                var tool = ActiveTool != null && ActiveTool.CallId == toolFinished.CallId
                    ? ActiveTool.Tool
                    : _proposedCalls.LastOrDefault(call => call.CallId == toolFinished.CallId)?.Tool;

                if (ActiveTool != null && ActiveTool.CallId == toolFinished.CallId)
                {
                    ActiveTool = null;
                }

                LastFinishedTool = tool;
                LastToolFirstLine = toolFinished.FirstLine;
                break;
            }
            case MonitorStatus status:
            {
                Status = new MonitorStatusPayload(
                    status.State,
                    status.ElapsedSeconds,
                    status.TurnsRemaining,
                    status.PendingCount,
                    status.LastActivity,
                    status.Run,
                    status.SessionTotals,
                    status.ContextTokens);
                break;
            }
            case MonitorTranscriptCommitted transcriptCommitted:
            {
                TranscriptCount = transcriptCommitted.Count;

                if (Phase == "turn_done")
                {
                    _reasoning.Clear();
                    _text.Clear();
                    _proposedCalls.Clear();
                }

                break;
            }
            case MonitorRunEnded runEnded:
            {
                EndedState = runEnded.State;
                ErrorMessage = runEnded.ErrorMessage;
                SetPhase("ended");
                break;
            }
        }
    }

    public MonitorSnapshot CreateSnapshot()
    {
        return CreateSnapshot(MonitorProtocol.LiveTextTailCharacters);
    }

    /// <summary>
    ///     Copies at most the last tailCharacters of the live reasoning and text. The character counts still cover the whole turn.
    /// </summary>
    public MonitorSnapshot CreateSnapshot(int tailCharacters)
    {
        var cap = Math.Clamp(tailCharacters, 0, MonitorProtocol.LiveTextTailCharacters);

        return new MonitorSnapshot(
            Phase,
            PhaseStartedAt,
            ModelCall,
            Tail(_reasoning, cap),
            ReasoningCharacters,
            Tail(_text, cap),
            TextCharacters,
            _proposedCalls.ToArray(),
            ActiveTool,
            Status,
            TranscriptCount,
            LastTurn);
    }

    public bool Equals(LiveRunState? other)
    {
        if (other == null)
        {
            return false;
        }

        return SessionDirectory == other.SessionDirectory
            && Session == other.Session
            && Workdir == other.Workdir
            && RunId == other.RunId
            && RunKind == other.RunKind
            && ProcessId == other.ProcessId
            && ModelAlias == other.ModelAlias
            && ModelId == other.ModelId
            && StartedAt == other.StartedAt
            && Phase == other.Phase
            && PhaseStartedAt == other.PhaseStartedAt
            && ModelCall == other.ModelCall
            && Reasoning == other.Reasoning
            && ReasoningCharacters == other.ReasoningCharacters
            && Text == other.Text
            && TextCharacters == other.TextCharacters
            && ProposedCalls.SequenceEqual(other.ProposedCalls)
            && ActiveTool == other.ActiveTool
            && Status == other.Status
            && TranscriptCount == other.TranscriptCount
            && LastTurn == other.LastTurn
            && LastFinishedTool == other.LastFinishedTool
            && LastToolFirstLine == other.LastToolFirstLine
            && LastTurnError == other.LastTurnError
            && EndedState == other.EndedState
            && ErrorMessage == other.ErrorMessage;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as LiveRunState);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(SessionDirectory, RunId, Phase, ModelCall, TranscriptCount);
    }

    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        Phase = snapshot.Phase;
        PhaseStartedAt = snapshot.PhaseStartedAt;
        ModelCall = snapshot.ModelCall;
        SetTail(_reasoning, snapshot.Reasoning);
        ReasoningCharacters = snapshot.ReasoningCharacters;
        SetTail(_text, snapshot.Text);
        TextCharacters = snapshot.TextCharacters;
        _proposedCalls.Clear();
        _proposedCalls.AddRange(snapshot.ProposedCalls.Take(MonitorProtocol.MaxProposedCalls));
        ActiveTool = snapshot.ActiveTool;
        Status = snapshot.Status;
        TranscriptCount = snapshot.TranscriptCount;
        LastTurn = snapshot.LastTurn;
        TurnStartedAt = null;
        LastFinishedTool = null;
        LastToolFirstLine = null;
        LastTurnError = null;
        EndedState = null;
        ErrorMessage = null;
    }

    private void SetPhase(string phase, DateTimeOffset? at = null)
    {
        if (Phase == phase)
        {
            return;
        }

        Phase = phase;
        PhaseStartedAt = WireTime(at ?? DateTimeOffset.UtcNow);
    }

    private static DateTimeOffset WireTime(DateTimeOffset value)
    {
        return DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());
    }

    private static void AppendTail(StringBuilder builder, string text)
    {
        builder.Append(text);
        var cap = MonitorProtocol.LiveTextTailCharacters;

        if (builder.Length <= cap * 2)
        {
            return;
        }

        var remove = builder.Length - cap;

        if (remove < builder.Length && char.IsLowSurrogate(builder[remove]) && char.IsHighSurrogate(builder[remove - 1]))
        {
            remove++;
        }

        builder.Remove(0, remove);
    }

    private static string Tail(StringBuilder builder, int cap)
    {
        if (builder.Length <= cap)
        {
            return builder.ToString();
        }

        var start = builder.Length - cap;

        if (start > 0 && char.IsLowSurrogate(builder[start]) && char.IsHighSurrogate(builder[start - 1]))
        {
            start++;
        }

        return builder.ToString(start, builder.Length - start);
    }

    private static void SetTail(StringBuilder builder, string text)
    {
        builder.Clear();
        var cap = MonitorProtocol.LiveTextTailCharacters;

        if (text.Length <= cap)
        {
            builder.Append(text);
            return;
        }

        var start = text.Length - cap;

        if (char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
        {
            start++;
        }

        builder.Append(text, start, text.Length - start);
    }
}

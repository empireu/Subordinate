namespace KnowledgeSystem.Agents.Orchestration.Tools;

/// <summary>
///     Decides whether a plain tool call may execute. Consulted by the runner immediately before the handler runs.
/// </summary>
public interface IToolCallGate<TContext> where TContext : AgentExecutionContext
{
    /// <summary>
    ///     Evaluates a tool call that is about to start. Exceptions are treated as a denial.
    /// </summary>
    Task<ToolGateVerdict> EvaluateAsync(AgentRunner<TContext> runner, AgentToolFrame.PlainRunningFrame frame, CancellationToken cancellationToken);
}

/// <summary>
///     The outcome of a gate evaluation.
/// </summary>
public abstract class ToolGateVerdict
{
    private ToolGateVerdict()
    {
    }

    public static readonly ToolGateVerdict Allow = new AllowVerdict();

    public static ToolGateVerdict Deny(string message)
    {
        return new DenyVerdict(message);
    }

    public static ToolGateVerdict Defer(ToolDeferral deferral)
    {
        return new DeferVerdict(deferral);
    }

    /// <summary>
    ///     The call executes normally.
    /// </summary>
    public sealed class AllowVerdict : ToolGateVerdict
    {
        internal AllowVerdict()
        {
        }
    }

    /// <summary>
    ///     The call does not execute, and the message is reported to the LLM as a failed tool result.
    /// </summary>
    public sealed class DenyVerdict : ToolGateVerdict
    {
        internal DenyVerdict(string message)
        {
            Message = message;
        }

        public string Message { get; }
    }

    /// <summary>
    ///     The call does not execute, and the frame is settled with a <see cref="ToolDeferral"/> instead of a result.
    /// </summary>
    public sealed class DeferVerdict : ToolGateVerdict
    {
        internal DeferVerdict(ToolDeferral deferral)
        {
            Deferral = deferral;
        }

        public ToolDeferral Deferral { get; }
    }
}

/// <summary>
///     A tool call that was received but deliberately not executed, because it is waiting for an outside decision.
///     This is not a <see cref="ToolExecutionResult"/>, because the tool never ran.
/// </summary>
public sealed class ToolDeferral
{
    public ToolDeferral(string message, object? payload = null)
    {
        Message = message;
        Payload = payload;
    }

    /// <summary>
    ///     The text reported to the LLM in place of a tool result.
    /// </summary>
    public string Message { get; }

    /// <summary>
    ///     Optional data attached by the gate, such as the pending approval record.
    /// </summary>
    public object? Payload { get; }
}

/// <summary>
///     The persisted state of one tool call, used to rebuild an interrupted batch of tool calls.
/// </summary>
public sealed class RestoredToolCall
{
    private RestoredToolCall(ToolExecutionResult? result, ToolDeferral? deferral)
    {
        Result = result;
        Deferral = deferral;
    }

    public ToolExecutionResult? Result { get; }

    public ToolDeferral? Deferral { get; }

    public static RestoredToolCall Completed(ToolExecutionResult result)
    {
        return new RestoredToolCall(result, null);
    }

    public static RestoredToolCall Deferred(ToolDeferral deferral)
    {
        return new RestoredToolCall(null, deferral);
    }
}

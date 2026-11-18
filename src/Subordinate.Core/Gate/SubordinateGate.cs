using KnowledgeSystem.Agents.Orchestration;
using KnowledgeSystem.Agents.Orchestration.Tools;
using Subordinate.Core.Classification;
using Subordinate.Core.Policy;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;
using Subordinate.Core.Tools;

namespace Subordinate.Core.Gate;

/// <summary>
///     The single permission check for every tool call. Allows what the policy and grants cover, denies what was denied before, and defers everything else as a pending approval.
/// </summary>
public sealed class SubordinateGate(PendingLedger ledger, Func<string> currentRunId, Func<string, Task> beforeExecute) : IToolCallGate<SubordinateContext>
{
    public PendingLedger Ledger { get; } = ledger;

    /// <summary>
    ///     When this returns a message, new tool calls are refused with it without classification.
    /// </summary>
    public Func<string?>? RefuseNewCalls { get; init; }

    public async Task<ToolGateVerdict> EvaluateAsync(AgentRunner<SubordinateContext> runner, AgentToolFrame.PlainRunningFrame frame, CancellationToken cancellationToken)
    {
        if (!runner.Agent.ToolRegistry.Handlers.TryGetValue(frame.Tool, out var handler) || handler is not IGatedTool gatedTool)
        {
            return ToolGateVerdict.Deny($"The tool {frame.Tool.ToolId} has no permission classification, so it cannot run.");
        }

        var environment = runner.ExecutionContext.Environment;
        var existing = Ledger.FindByCallId(frame.CallId);

        if (existing != null)
        {
            if (existing.Status == PendingStatuses.Approved && existing.Execution == null)
            {
                existing.Execution = new ExecutionRecord { State = "started", At = DateTimeOffset.UtcNow };
                await beforeExecute(frame.CallId);
                return ToolGateVerdict.Allow;
            }

            if (existing.Status == PendingStatuses.Denied)
            {
                return ToolGateVerdict.Deny(PendingLedger.BuildDenialMessage(existing));
            }

            if (existing.Status == PendingStatuses.Pending)
            {
                return ToolGateVerdict.Defer(new ToolDeferral(existing.DeferralMessage, existing));
            }
        }

        if (RefuseNewCalls?.Invoke() is { } refusal)
        {
            return ToolGateVerdict.Deny(refusal);
        }

        var fingerprint = gatedTool.Fingerprint(frame.Args, environment);
        var denied = Ledger.FindDenied(fingerprint);

        if (denied != null)
        {
            return ToolGateVerdict.Deny(PendingLedger.BuildDenialMessage(denied));
        }

        var open = Ledger.FindOpen(fingerprint);

        if (open != null)
        {
            return ToolGateVerdict.Deny(PendingLedger.BuildDuplicateMessage(open));
        }

        var classification = gatedTool.Classify(frame.Args, environment);
        var decision = PolicyEvaluator.Evaluate(classification, Ledger.Policy, Ledger.Grants);

        if (decision.IsAllowed)
        {
            await beforeExecute(frame.CallId);
            return ToolGateVerdict.Allow;
        }

        var item = Ledger.Add(
            frame.CallId,
            frame.Tool.ToolId,
            currentRunId(),
            SerializeArguments(frame),
            fingerprint,
            gatedTool.Describe(frame.Args, environment),
            classification,
            decision,
            environment.Shell.ToToken());

        return ToolGateVerdict.Defer(new ToolDeferral(item.DeferralMessage, item));
    }

    private static string SerializeArguments(AgentToolFrame.RunningFrame frame)
    {
        var values = new Dictionary<string, object?>();

        foreach (var (argument, value) in frame.Args.Arguments)
        {
            if (value != null)
            {
                values[argument.ArgumentName] = value;
            }
        }

        return System.Text.Json.JsonSerializer.Serialize(values);
    }
}

using KnowledgeSystem.Agents.Orchestration;
using KnowledgeSystem.Agents.Orchestration.Tools;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Classification;
using Subordinate.Core.Platform;
using Subordinate.Core.Policy;
using Subordinate.Core.Tools;

namespace Subordinate.Core.Running;

/// <summary>
///     The headless subagent: four gated tools and a final plain-text answer.
/// </summary>
public sealed class SubordinateAgent : Agent<SubordinateContext>
{
    public SubordinateAgent(ShellKind shell, IToolCallGate<SubordinateContext> gate) : base("subordinate")
    {
        ShellToolHandler.Register(ToolRegistry, shell);
        ReadFileToolHandler.Register(ToolRegistry);
        WriteFileToolHandler.Register(ToolRegistry);
        EditFileToolHandler.Register(ToolRegistry);
        ToolGate = gate;
    }

    /// <summary>
    ///     When this returns true, a reply without tool calls does not end the runner.
    /// </summary>
    public Func<bool>? KeepRunningAfterAnswer { get; set; }

    public override Task<AgentCallbackResult> HandleCompletion(AgentRunner<SubordinateContext> runner, ChatMessage response)
    {
        runner.ExecutionContext.InsertAssistantCompletion(response);

        if (KeepRunningAfterAnswer?.Invoke() == true)
        {
            return Task.FromResult(AgentCallbackResult.Continue);
        }

        return Task.FromResult(AgentCallbackResult.Break);
    }

    public static string BuildSystemPrompt(string basePrompt, string workdir, ShellKind shell, PolicySet policy, string? supervisorInstructions)
    {
        var shellText = PlatformServices.Current.Shells.PromptDescription(shell);

        var grants = policy.CommandGrants.Count > 0
            ? " Additionally allowed commands: " + string.Join("; ", policy.CommandGrants) + "."
            : string.Empty;

        var prompt = $"""
            {basePrompt.Trim()}

            # Session

            Workdir: {workdir}
            Shell: {shellText}
            Automatically allowed: {string.Join(", ", policy.Capabilities.ToTokens())}.{grants}
            """;

        if (!string.IsNullOrWhiteSpace(supervisorInstructions))
        {
            prompt += "\n\n# Instructions from the supervisor\n\n" + supervisorInstructions.Trim();
        }

        return prompt;
    }

    public const string DefaultBasePrompt = """
        You are a subagent working for a supervising coding agent. You run headless: nobody reads your messages until you finish, and nobody can answer questions mid-task.

        Tools: shell runs one command, read_file reads a file with line numbers, write_file writes a whole file, edit_file replaces exact text. Prefer read_file over cat, and rg over grep for searching.

        Permissions: every tool call is checked against a fixed policy. Reading inside the workdir is normally allowed. A call outside the policy is not executed; its result starts with PENDING APPROVAL and an id. Do not repeat that call. Continue with whatever you can do without it. If you are blocked, end your turn and state exactly what you need and why; the supervisor will approve or deny and resume you, and approved calls are run for you with their output delivered in a later message. A result that starts with DENIED means the supervisor refused; never retry it.

        Commands run in a fresh non-interactive shell each time, with standard input closed: no pagers, editors or prompts. Shell state such as the current directory or variables does not carry over between calls; use the workdir argument instead of cd. Avoid loops and variable assignments in commands, because they cannot be checked statically and always need approval.

        When you are done, reply with your final answer as plain text without calling tools. The final answer is the only thing the supervisor reads, so make it complete: findings, evidence with file paths and line numbers, and anything left undone.
        """;
}

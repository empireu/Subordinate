using KnowledgeSystem.Agents.Context;
using KnowledgeSystem.Agents.Orchestration;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Classification;
using Subordinate.Core.Platform;
using Subordinate.Core.Shells;

namespace Subordinate.Core.Running;

/// <summary>
///     Facts the tools need about the session they run in.
/// </summary>
public sealed class ToolEnvironment
{
    public required string Workdir { get; init; }

    public required ShellKind Shell { get; init; }

    public required ICommandExecutor Executor { get; init; }

    /// <summary>
    ///     The run directory that receives full tool output when it is truncated.
    /// </summary>
    public required string OutputDirectory { get; set; }

    /// <summary>
    ///     Directories the subagent may read without the read-outside capability.
    /// </summary>
    public required IReadOnlyList<string> ReadableRoots { get; set; }

    public required int DefaultShellTimeoutSeconds { get; init; }

    public HashSet<string> FilesWritten { get; } = new(PlatformServices.Current.Paths.Comparer);

    private int _outputCounter;

    public string NextOutputStem()
    {
        var number = Interlocked.Increment(ref _outputCounter);
        return $"{DateTime.UtcNow:HHmmss}-{number:D3}";
    }

    public ClassificationScope CreateScope(string workingDirectory)
    {
        return new ClassificationScope
        {
            Workdir = Workdir,
            WorkingDirectory = workingDirectory,
            ReadableRoots = ReadableRoots
        };
    }
}

/// <summary>
///     The execution context of the subagent: the conversation plus the tool environment.
/// </summary>
public sealed class SubordinateContext(ToolEnvironment environment) : AgentExecutionContext
{
    public AgentContext Timeline { get; } = new();

    public ToolEnvironment Environment { get; } = environment;

    public override IReadOnlyList<ChatMessage> ChatMessages => Timeline.ChatMessages;

    public override void InsertAssistantCompletion(ChatMessage message)
    {
        Timeline.InsertAssistant(message);
    }

    public override void InsertToolResult(string toolCallId, string toolName, string output)
    {
        Timeline.InsertChat(new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents =
            [
                new ToolResultContent
                {
                    CallId = toolCallId,
                    ToolName = toolName,
                    Output = output
                }
            ]
        });
    }

    public void InsertUserMessage(string text)
    {
        InsertUserMessage(text, null);
    }

    public void InsertUserMessage(string text, string? authorName)
    {
        Timeline.InsertChat(new ChatMessage
        {
            Role = ChatRole.User,
            AuthorName = authorName,
            Contents = [new TextContent { Text = text }]
        });
    }
}

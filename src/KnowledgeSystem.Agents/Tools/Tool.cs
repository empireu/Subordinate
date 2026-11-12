using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace KnowledgeSystem.Agents.Tools;

/// <summary>
///     Wraps an AI tool definition.
/// </summary>
public sealed class AgentTool
{
    public required string ToolId { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ToolArgument> Arguments { get; init; }
    public required IReadOnlyList<ToolArgument> RequiredArguments { get; init; }
    public required JsonElement ParametersSchema { get; init; }

    public ChatTool CreateChatTool() => new()
    {
        Name = ToolId,
        Description = Description,
        ParametersSchema = ParametersSchema
    };
}

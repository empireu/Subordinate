using System.Text.Json;

namespace KnowledgeSystem.Ai.Providers.Abstractions;

public sealed class ChatTool
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required JsonElement ParametersSchema { get; init; }
    public bool? Strict { get; init; }
}

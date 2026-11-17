using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace Subordinate.Core.Running;

public static class ToolCallSummary
{
    public static string Describe(ToolCallContent call)
    {
        if (call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty("command", out var command))
        {
            return command.GetString() ?? string.Empty;
        }

        if (call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty("path", out var path))
        {
            return path.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}

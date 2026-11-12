using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace KnowledgeSystem.Agents.Context;

/// <summary>
///     Normalizes message/content access so content shapes are handled in one place.
/// </summary>
public static class ChatMessageHelpers
{
    public static IEnumerable<string> EnumerateTextContents(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent:
                    yield return textContent.Text;
                    break;
                case ToolResultContent resultContent:
                    yield return resultContent.Output;
                    break;
            }
        }
    }

    public static bool TryGetToolResultText(
        ChatMessage message,
        out string text,
        out string? callId,
        out string? toolName
    )
    {
        foreach (var content in message.Contents)
        {
            if (content is ToolResultContent resultContent)
            {
                text = resultContent.Output;
                callId = resultContent.CallId;
                toolName = resultContent.ToolName;
                return true;
            }
        }

        text = string.Empty;
        callId = null;
        toolName = null;
        return false;
    }

    public static string? FormatContentValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            JsonElement jsonElement => jsonElement.ValueKind == JsonValueKind.String
                ? jsonElement.GetString()
                : jsonElement.GetRawText(),
            _ => value.ToString()
        };
    }
}

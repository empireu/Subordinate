using System.Text;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace KnowledgeSystem.Agents.Context;

/// <summary>
///     Wraps an AI message, that gets sent to the LLM.
/// </summary>
public sealed class ChatElement(ChatMessage message) : ITimelineElement
{
    public readonly ChatMessage Message = message;

    public string ToLogFormat()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"{Message.Role}:");

        if (Message.AuthorName != null)
        {
            sb.AppendLine($"  Name: {Message.AuthorName}");
        }

        foreach (var content in Message.Contents)
        {
            switch (content)
            {
                case TextContent textContent:
                {
                    foreach (var line in textContent.Text.EnumerateLines())
                    {
                        sb.Append("  ");
                        sb.Append(line);
                        sb.AppendLine();
                    }

                    break;
                }
                case ReasoningContent reasoningContent:
                {
                    sb.AppendLine($"  [Reasoning] {reasoningContent.Text}");
                    break;
                }
                case ToolCallContent toolCall:
                {
                    sb.AppendLine($"  [ToolCall] {toolCall.ToolName}({toolCall.Arguments.GetRawText()})");
                    break;
                }
                case ToolResultContent resultContent:
                {
                    sb.AppendLine($"  ToolCallId: {resultContent.CallId}");
                    sb.AppendLine($"  Result: {resultContent.Output}");
                    break;
                }
                case ImageContent imageContent:
                {
                    sb.AppendLine($"  [Image] {imageContent.Url}");
                    break;
                }
                default:
                    sb.AppendLine($"  [{content.GetType().Name}] (unknown)");
                    break;
            }
        }

        return sb.ToString();
    }
}

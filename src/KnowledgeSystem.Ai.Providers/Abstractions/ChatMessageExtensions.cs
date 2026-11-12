using System.Text;

namespace KnowledgeSystem.Ai.Providers.Abstractions;

public static class ChatMessageExtensions
{
    public static string GetText(this ChatMessage message)
    {
        var builder = new StringBuilder();

        foreach (var content in message.Contents)
        {
            if (content is TextContent text)
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }

    public static string GetReasoningText(this ChatMessage message)
    {
        var builder = new StringBuilder();

        foreach (var content in message.Contents)
        {
            if (content is ReasoningContent reasoning)
            {
                builder.Append(reasoning.Text);
            }
        }

        return builder.ToString();
    }

    public static IReadOnlyList<ToolCallContent> GetToolCalls(this ChatMessage message)
    {
        return message.Contents.OfType<ToolCallContent>().ToArray();
    }
}

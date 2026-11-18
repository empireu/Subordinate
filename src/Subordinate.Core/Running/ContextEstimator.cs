using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace Subordinate.Core.Running;

/// <summary>
///     Estimates the size of a conversation in tokens without a tokenizer.
/// </summary>
public static class ContextEstimator
{
    public static long Estimate(IEnumerable<ChatMessage> messages)
    {
        long characters = 0;
        var count = 0;

        foreach (var message in messages)
        {
            count++;

            foreach (var content in message.Contents)
            {
                characters += content switch
                {
                    TextContent text => text.Text.Length,
                    ReasoningContent reasoning => reasoning.Text?.Length ?? 0,
                    ToolCallContent call => call.Arguments.ValueKind == JsonValueKind.Undefined ? 0 : call.Arguments.GetRawText().Length,
                    ToolResultContent result => result.Output.Length,
                    _ => 0
                };
            }
        }

        return characters / 4 + count * 4L;
    }

    /// <summary>
    ///     Estimates the active context at a boundary, reusing the provider's last reported token count when it is known.
    /// </summary>
    public static long EstimateActiveContext(IReadOnlyList<ChatMessage> messages, long contextTokens)
    {
        if (contextTokens <= 0)
        {
            return Estimate(messages);
        }

        var lastAssistantIndex = -1;

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.Assistant)
            {
                lastAssistantIndex = index;
                break;
            }
        }

        return contextTokens + Estimate(messages.Skip(lastAssistantIndex + 1));
    }
}

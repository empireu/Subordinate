using System.Runtime.CompilerServices;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Monitoring;

namespace Subordinate.Core.Running;

public sealed class MonitoredChatProvider(IChatProvider inner, IMonitorFeed feed) : IChatProvider
{
    private int _modelCalls;

    public string ProviderId => inner.ProviderId;

    public string? DefaultModel => inner.DefaultModel;

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var modelCall = Interlocked.Increment(ref _modelCalls);
        feed.Publish(new MonitorTurnStarted(modelCall, DateTimeOffset.UtcNow));
        ChatUsage? usage = null;

        await foreach (var streamEvent in inner.StreamAsync(request, cancellationToken))
        {
            switch (streamEvent)
            {
                case ReasoningDeltaEvent reasoningDelta:
                {
                    feed.Publish(new MonitorReasoningDelta(reasoningDelta.Delta));
                    break;
                }
                case TextDeltaEvent textDelta:
                {
                    feed.Publish(new MonitorTextDelta(textDelta.Delta));
                    break;
                }
                case ToolCallCompletedEvent toolCallCompleted:
                {
                    feed.Publish(new MonitorToolCallProposed(
                        toolCallCompleted.ToolCall.CallId,
                        toolCallCompleted.ToolCall.ToolName,
                        Truncate(ToolCallSummary.Describe(toolCallCompleted.ToolCall), MonitorProtocol.MaxSummaryCharacters)));
                    break;
                }
                case UsageReceivedEvent usageReceived:
                {
                    usage = usageReceived.Usage;
                    break;
                }
                case StreamCompletedEvent completed:
                {
                    feed.Publish(new MonitorTurnCompleted(
                        FinishReasonToken(completed.FinishReason),
                        (long?)usage?.InputTokens,
                        (long?)usage?.CachedInputTokens,
                        (long?)usage?.OutputTokens,
                        DateTimeOffset.UtcNow));
                    break;
                }
                case StreamErrorEvent error:
                {
                    feed.Publish(new MonitorTurnFailed(Truncate(error.Error.Message, MonitorProtocol.MaxMessageCharacters)));
                    break;
                }
            }

            yield return streamEvent;
        }
    }

    public async Task<ChatMessage> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ChatMessage? message = null;

        await foreach (var streamEvent in StreamAsync(request, cancellationToken))
        {
            switch (streamEvent)
            {
                case StreamCompletedEvent completed:
                {
                    message = completed.Message;
                    break;
                }
                case StreamErrorEvent error:
                {
                    throw new ChatProviderException(error.Error);
                }
            }
        }

        return message ?? throw new InvalidOperationException("The provider did not return a completed message.");
    }

    private static string FinishReasonToken(ChatFinishReason reason)
    {
        return reason switch
        {
            ChatFinishReason.Stop => "stop",
            ChatFinishReason.Length => "length",
            ChatFinishReason.ToolCalls => "tool_calls",
            ChatFinishReason.ContentFilter => "content_filter",
            ChatFinishReason.Error => "error",
            ChatFinishReason.Aborted => "aborted",
            _ => "unknown"
        };
    }

    private static string Truncate(string text, int length)
    {
        return text.Length <= length ? text : text[..length];
    }
}

using System.Runtime.CompilerServices;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace Subordinate.Core.Running;

/// <summary>
///     Counts model calls and token usage of the wrapped provider.
/// </summary>
public sealed class CountingChatProvider(IChatProvider inner) : IChatProvider
{
    private int _modelCalls;
    private long _inputTokens;
    private long _cachedInputTokens;
    private long _outputTokens;

    public string ProviderId => inner.ProviderId;

    public string? DefaultModel => inner.DefaultModel;

    public int ModelCalls => Volatile.Read(ref _modelCalls);

    public long InputTokens => Interlocked.Read(ref _inputTokens);

    public long CachedInputTokens => Interlocked.Read(ref _cachedInputTokens);

    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    public bool IsStreaming { get; private set; }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _modelCalls);
        IsStreaming = true;

        try
        {
            await foreach (var streamEvent in inner.StreamAsync(request, cancellationToken))
            {
                if (streamEvent is UsageReceivedEvent usage)
                {
                    Interlocked.Add(ref _inputTokens, usage.Usage.InputTokens ?? 0);
                    Interlocked.Add(ref _cachedInputTokens, usage.Usage.CachedInputTokens ?? 0);
                    Interlocked.Add(ref _outputTokens, usage.Usage.OutputTokens ?? 0);
                }

                yield return streamEvent;
            }
        }
        finally
        {
            IsStreaming = false;
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
}

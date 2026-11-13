using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Ai.Providers.Configuration;

namespace KnowledgeSystem.Ai.Providers.Protocols.Mistral;

public sealed class MistralChatCompletionsProvider : IChatProvider
{
    private readonly ProviderConfig _config;
    private readonly IChatProtocol _protocol;

    public MistralChatCompletionsProvider(ProviderConfig config, IChatProtocol protocol)
    {
        _config = config;
        _protocol = protocol;
    }

    public string ProviderId => _config.ProviderId;

    public string? DefaultModel => _config.DefaultModel;

    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        CancellationToken cancellationToken
    )
    {
        return _protocol.StreamAsync(
            new ChatProtocolRequest
            {
                Endpoint = new Uri(_config.Endpoint),
                ApiKey = _config.ApiKey,
                Request = ResolveRequest(request),
                Headers = _config.Headers,
                NetworkTimeoutSeconds = _config.NetworkTimeoutSeconds
            },
            cancellationToken
        );
    }

    public async Task<ChatMessage> CompleteAsync(
        ChatRequest request,
        CancellationToken cancellationToken
    )
    {
        ChatMessage? message = null;

        await foreach (var streamEvent in StreamAsync(request, cancellationToken))
        {
            switch (streamEvent)
            {
                case StreamCompletedEvent completed:
                    message = completed.Message;
                    break;
                case StreamErrorEvent error:
                    throw new ChatProviderException(error.Error);
            }
        }

        return message ?? throw new InvalidOperationException("The provider did not return a completed message.");
    }

    private ChatRequest ResolveRequest(ChatRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            return request;
        }

        if (string.IsNullOrWhiteSpace(_config.DefaultModel))
        {
            throw new InvalidOperationException($"No model was specified for provider \"{_config.ProviderId}\".");
        }

        return new ChatRequest
        {
            Model = _config.DefaultModel,
            Messages = request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxOutputTokens,
            ReasoningEffort = request.ReasoningEffort,
            PromptCacheKey = request.PromptCacheKey
        };
    }
}

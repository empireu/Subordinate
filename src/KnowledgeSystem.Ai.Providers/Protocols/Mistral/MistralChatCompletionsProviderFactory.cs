using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Ai.Providers.Configuration;

namespace KnowledgeSystem.Ai.Providers.Protocols.Mistral;

public sealed class MistralChatCompletionsProviderFactory : IProviderFactory
{
    public string ProtocolId => MistralChatCompletionsProtocol.ProtocolIdValue;

    public IChatProvider Create(ProviderConfig config)
    {
        return new MistralChatCompletionsProvider(
            config,
            new MistralChatCompletionsProtocol()
        );
    }
}

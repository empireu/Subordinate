using KnowledgeSystem.Ai.Providers.Configuration;

namespace KnowledgeSystem.Ai.Providers.Abstractions;

public interface IProviderFactory
{
    string ProtocolId { get; }

    IChatProvider Create(ProviderConfig config);
}

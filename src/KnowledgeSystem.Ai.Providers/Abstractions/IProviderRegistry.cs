namespace KnowledgeSystem.Ai.Providers.Abstractions;

public interface IProviderRegistry
{
    void Register(IChatProvider provider);

    IChatProvider Get(string providerId);

    bool TryGet(string providerId, out IChatProvider? provider);
}

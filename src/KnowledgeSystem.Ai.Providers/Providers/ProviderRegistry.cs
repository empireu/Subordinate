using KnowledgeSystem.Ai.Providers.Abstractions;

namespace KnowledgeSystem.Ai.Providers.Providers;

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IChatProvider provider)
    {
        _providers[provider.ProviderId] = provider;
    }

    public IChatProvider Get(string providerId)
    {
        if (!_providers.TryGetValue(providerId, out var provider))
        {
            throw new KeyNotFoundException($"The provider \"{providerId}\" is not registered.");
        }

        return provider;
    }

    public bool TryGet(string providerId, out IChatProvider? provider)
    {
        return _providers.TryGetValue(providerId, out provider);
    }
}

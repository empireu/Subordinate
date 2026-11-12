using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Ai.Providers.Configuration;

namespace KnowledgeSystem.Ai.Providers.Providers;

public sealed class ProviderFactoryRegistry
{
    private readonly Dictionary<string, IProviderFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IProviderFactory factory)
    {
        _factories[factory.ProtocolId] = factory;
    }

    public IChatProvider Create(ProviderConfig config)
    {
        if (!_factories.TryGetValue(config.Protocol, out var factory))
        {
            throw new KeyNotFoundException($"The protocol \"{config.Protocol}\" is not registered.");
        }

        return factory.Create(config);
    }
}

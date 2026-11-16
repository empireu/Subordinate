using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Ai.Providers.Configuration;
using KnowledgeSystem.Ai.Providers.Protocols.Mistral;
using KnowledgeSystem.Ai.Providers.Providers;
using Subordinate.Core.Running;

namespace Subordinate.Core.Configuration;

public sealed class ProviderEntry
{
    public string Protocol { get; set; } = MistralChatCompletionsProtocol.ProtocolIdValue;

    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    ///     The name of the environment variable holding the API key. Preferred over <see cref="ApiKey"/>.
    /// </summary>
    public string? ApiKeyEnv { get; set; }

    public string? ApiKey { get; set; }

    public Dictionary<string, string> Headers { get; set; } = [];

    public int? NetworkTimeoutSeconds { get; set; }
}

public sealed class ModelEntry
{
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public float? Temperature { get; set; }

    public int? MaxOutputTokens { get; set; }

    public string? ReasoningEffort { get; set; }
}

public sealed class ShellPathsEntry
{
    public string? Bash { get; set; }

    public string? Powershell { get; set; }

    public string? Pwsh { get; set; }
}

public sealed class DefaultsEntry
{
    public string Shell { get; set; } = "bash";

    public int MaxTurns { get; set; } = 100;

    public int MaxMinutes { get; set; } = 60;

    public int ShellTimeoutSeconds { get; set; } = 120;

    public int KeepRuns { get; set; } = 10;
}

/// <summary>
///     The user configuration, read from config.json in the Subordinate home directory.
/// </summary>
public sealed class SubordinateConfig
{
    public const string FileName = "config.json";

    public const string BasePromptFileName = "system-prompt.md";

    private const int MaxBasePromptCharacters = 200_000;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Dictionary<string, ProviderEntry> Providers { get; set; } = [];

    public Dictionary<string, ModelEntry> Models { get; set; } = [];

    public string? DefaultModel { get; set; }

    public ShellPathsEntry Shells { get; set; } = new();

    public DefaultsEntry Defaults { get; set; } = new();

    public static string ResolveHome()
    {
        var overridden = Environment.GetEnvironmentVariable("SUBORDINATE_HOME");

        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrEmpty(userProfile))
        {
            throw new SubordinateException(ErrorCode.ConfigError, "The home directory is unknown. Set SUBORDINATE_HOME.");
        }

        return Path.Combine(userProfile, ".subordinate");
    }

    public static SubordinateConfig Load(string home)
    {
        var path = Path.Combine(home, FileName);

        if (!File.Exists(path))
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"No configuration found at {path}. Run 'subordinate init' to create a template, then fill in a provider and a model.");
        }

        SubordinateConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<SubordinateConfig>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"The configuration at {path} is not valid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"The configuration at {path} could not be read: {ex.Message}");
        }

        return config ?? throw new SubordinateException(ErrorCode.ConfigError, $"The configuration at {path} is empty.");
    }

    /// <summary>
    ///     Reads the base system prompt from system-prompt.md in the home directory, or returns the built-in default when the file does not exist.
    /// </summary>
    public static (string Text, string? Path) LoadBasePrompt(string home)
    {
        var path = System.IO.Path.Combine(home, BasePromptFileName);

        if (!File.Exists(path))
        {
            return (SubordinateAgent.DefaultBasePrompt, null);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"The base system prompt at {path} could not be read: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"The base system prompt at {path} is empty. Delete it to use the built-in prompt.");
        }

        if (text.Length > MaxBasePromptCharacters)
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"The base system prompt at {path} is longer than {MaxBasePromptCharacters} characters.");
        }

        return (text, path);
    }

    public static string CreateTemplate()
    {
        var template = new SubordinateConfig
        {
            Providers =
            {
                ["mistral"] = new ProviderEntry
                {
                    Endpoint = "https://api.mistral.ai/v1",
                    ApiKeyEnv = "MISTRAL_API_KEY"
                }
            },
            Models =
            {
                ["default"] = new ModelEntry
                {
                    Provider = "mistral",
                    Model = "REPLACE-WITH-MODEL-ID"
                }
            },
            DefaultModel = "default"
        };

        return JsonSerializer.Serialize(template, JsonOptions).ReplaceLineEndings("\n");
    }

    /// <summary>
    ///     Resolves a model alias to its entry. Fails with <see cref="ErrorCode.BadModel"/> for an unknown alias.
    /// </summary>
    public (string Alias, ModelEntry Model, ProviderEntry Provider) ResolveModel(string? alias)
    {
        var effective = alias ?? DefaultModel;

        if (string.IsNullOrWhiteSpace(effective))
        {
            throw new SubordinateException(ErrorCode.BadModel, "No model was given and the configuration has no default_model.");
        }

        if (!Models.TryGetValue(effective, out var model))
        {
            var known = Models.Count == 0 ? "none are configured" : "known aliases: " + string.Join(", ", Models.Keys.Order());
            throw new SubordinateException(ErrorCode.BadModel, $"Unknown model alias \"{effective}\"; {known}.");
        }

        if (!Providers.TryGetValue(model.Provider, out var provider))
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"Model \"{effective}\" refers to provider \"{model.Provider}\", which is not configured.");
        }

        if (string.IsNullOrWhiteSpace(model.Model))
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"Model \"{effective}\" has no model id.");
        }

        return (effective, model, provider);
    }

    /// <summary>
    ///     Creates the chat provider for a model. The API key is read from the environment or the configuration and is never echoed.
    /// </summary>
    public static IChatProvider CreateProvider(string providerName, ProviderEntry provider, ModelEntry model)
    {
        var apiKey = provider.ApiKey;

        if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnv))
        {
            apiKey = Environment.GetEnvironmentVariable(provider.ApiKeyEnv);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new SubordinateException(ErrorCode.ConfigError, $"The environment variable {provider.ApiKeyEnv} for provider \"{providerName}\" is not set.");
            }
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"Provider \"{providerName}\" has no api_key_env or api_key.");
        }

        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out _))
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"Provider \"{providerName}\" has an invalid endpoint.");
        }

        var registry = new ProviderFactoryRegistry();
        registry.Register(new MistralChatCompletionsProviderFactory());

        try
        {
            return registry.Create(new ProviderConfig
            {
                ProviderId = providerName,
                Protocol = provider.Protocol,
                Endpoint = provider.Endpoint,
                ApiKey = apiKey,
                DefaultModel = model.Model,
                Headers = provider.Headers,
                NetworkTimeoutSeconds = provider.NetworkTimeoutSeconds
            });
        }
        catch (KeyNotFoundException)
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"Provider \"{providerName}\" uses the unsupported protocol \"{provider.Protocol}\".");
        }
    }

    public ChatRequestOptions CreateRequestOptions(ModelEntry model)
    {
        return new ChatRequestOptions
        {
            Model = model.Model,
            Temperature = model.Temperature,
            MaxOutputTokens = model.MaxOutputTokens,
            ReasoningEffort = model.ReasoningEffort
        };
    }
}

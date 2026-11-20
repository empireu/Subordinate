using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;
using Subordinate.Core.Monitoring;
using Subordinate.Core.Sessions;
using Subordinate.Core.Shells;

namespace Subordinate.Core.Running;

/// <summary>
///     Builds the provider, request options and executor for a session from its record and the configuration.
/// </summary>
public static class RunFactory
{
    public static SessionRunner CreateRunner(SubordinateConfig config, SessionStore store, SessionPaths paths, SessionRecord record, string runId)
    {
        var (_, model, provider) = config.ResolveModel(record.Model.Alias);
        ValidateAutocompaction(record.Model.Alias, model, config.Defaults);
        var images = CreateImageSettings(record.Model.Alias, model);

        if (!string.Equals(model.Model, record.Model.ModelId, StringComparison.Ordinal))
        {
            throw new SubordinateException(ErrorCode.BadModel, $"Model alias \"{record.Model.Alias}\" now points to {model.Model}, but this session started with {record.Model.ModelId}. Restore the alias or start a new session.");
        }

        var (noticeText, _) = SubordinateConfig.LoadAutocompactNotice(store.Home);
        var (requestText, _) = SubordinateConfig.LoadAutocompactPrompt(store.Home);
        var compaction = new CompactionSettings
        {
            Threshold = model.AutocompactThreshold,
            WindDownTurns = config.Defaults.AutocompactWindDownTurns,
            NoticeText = CompactionPrompts.FormatNotice(noticeText, config.Defaults.AutocompactWindDownTurns),
            RequestText = requestText
        };

        var chatProvider = SubordinateConfig.CreateProvider(record.Model.Provider, provider, model);

        if (!ShellKindNames.TryParse(record.Shell.Kind, out var kind))
        {
            throw new SubordinateException(ErrorCode.StateConflict, $"The session records an unknown shell \"{record.Shell.Kind}\".");
        }

        var shell = new ShellInfo(kind, record.Shell.ExecutablePath, record.Shell.Version);
        ShellLocator.VerifyUnchanged(shell);

        var secretVariables = new List<string>();
        foreach (var entry in config.Providers.Values)
        {
            if (!string.IsNullOrWhiteSpace(entry.ApiKeyEnv))
            {
                secretVariables.Add(entry.ApiKeyEnv);
            }
        }

        var executor = new ShellCommandExecutor(shell, secretVariables);
        var endpoint = MonitorEndpoint.Resolve(config, store.Home);
        var runInfo = new MonitorRunInfo(
            paths.Directory,
            record.Name,
            record.Workdir,
            runId,
            record.CurrentRun?.Kind ?? string.Empty,
            Environment.ProcessId,
            record.Model.Alias,
            record.Model.ModelId,
            record.CurrentRun?.StartedAt ?? DateTimeOffset.UtcNow);
        return new SessionRunner(store, paths, record, runId, chatProvider, config.CreateRequestOptions(model), executor, MonitorFeedFactory.Create(endpoint, runInfo), compaction, images);
    }

    /// <summary>
    ///     Validates the image settings of a model entry and builds the settings of its sessions.
    /// </summary>
    public static ImageSettings CreateImageSettings(string alias, ModelEntry model)
    {
        if (model.MaxImages is { } maxImages && maxImages < 1)
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"Model \"{alias}\" has max_images {maxImages}, which must be at least 1.");
        }

        if (model.MaxImages != null && !model.SupportsImages)
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"Model \"{alias}\" sets max_images but does not set supports_images.");
        }

        if (!model.SupportsImages)
        {
            return ImageSettings.Disabled;
        }

        return new ImageSettings
        {
            Enabled = true,
            MaxImages = model.MaxImages
        };
    }

    private static void ValidateAutocompaction(string alias, ModelEntry model, DefaultsEntry defaults)
    {
        if (model.AutocompactThreshold is { } threshold)
        {
            if (threshold < 1000)
            {
                throw new SubordinateException(ErrorCode.ConfigError, $"Model \"{alias}\" has autocompact_threshold {threshold}, which is below the minimum of 1000 tokens.");
            }

            if (model.ContextWindow is { } contextWindow && threshold >= contextWindow)
            {
                throw new SubordinateException(ErrorCode.ConfigError, $"Model \"{alias}\" has autocompact_threshold {threshold}, which must be less than its context_window of {contextWindow} tokens.");
            }
        }

        if (defaults.AutocompactWindDownTurns is < 0 or > 50)
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"The defaults set autocompact_wind_down_turns to {defaults.AutocompactWindDownTurns}, which must be between 0 and 50.");
        }
    }
}

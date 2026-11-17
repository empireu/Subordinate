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

        if (!string.Equals(model.Model, record.Model.ModelId, StringComparison.Ordinal))
        {
            throw new SubordinateException(ErrorCode.BadModel, $"Model alias \"{record.Model.Alias}\" now points to {model.Model}, but this session started with {record.Model.ModelId}. Restore the alias or start a new session.");
        }

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
        return new SessionRunner(store, paths, record, runId, chatProvider, config.CreateRequestOptions(model), executor, MonitorFeedFactory.Create(endpoint, runInfo));
    }
}

using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core;
using Subordinate.Core.Configuration;
using Subordinate.Core.Monitoring;
using Subordinate.Core.Platform;
using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;
using Subordinate.Tui.Live;
using Subordinate.Tui.Presentation;

namespace Subordinate.Tui;

internal sealed class TuiException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

internal static class TuiPrint
{
    public static int Run(TuiOptions options, TextWriter output)
    {
        var home = options.Home != null ? Path.GetFullPath(options.Home) : SubordinateConfig.ResolveHome();
        SubordinateConfig? config = null;

        try
        {
            config = SubordinateConfig.Load(home);
        }
        catch (SubordinateException)
        {
        }

        var endpoint = MonitorEndpoint.Resolve(config, home);
        var registry = new LiveSessionRegistry();
        MonitorServer? server = null;
        string feedStatus;

        try
        {
            if (options.WaitSeconds > 0)
            {
                server = new MonitorServer(endpoint, registry);
                server.Start();
                Thread.Sleep(TimeSpan.FromSeconds(options.WaitSeconds));
                feedStatus = server.Status;

                if (registry.HelloCount > 0)
                {
                    feedStatus += $", {registry.HelloCount} runs seen";
                }
            }
            else
            {
                feedStatus = !endpoint.Enabled ? endpoint.Problem ?? "disabled in config.json" : "not started";
            }

            var catalog = new SessionCatalog(home, directory => registry.IsConnected(directory));
            catalog.PollOnce();
            var now = DateTimeOffset.UtcNow;
            var sessions = SessionSelection.FilterAndSort(catalog.Snapshot.Sessions, options.All);

            if (options.Session != null)
            {
                var session = ResolveSession(catalog.Snapshot.Sessions, options.Session, options.Workdir);
                var entries = LoadEntries(session.Directory);
                var live = registry.GetSnapshot(session.Directory);
                var contextWindow = ResolveContextWindow(config, session.ModelAlias);
                output.Write(PrintRenderer.RenderDetail(home, feedStatus, session, entries, live, contextWindow, options.Width, now));
            }
            else
            {
                output.Write(PrintRenderer.RenderList(home, feedStatus, sessions, session => registry.GetSnapshot(session.Directory), options.Width, now));
            }

            return 0;
        }
        finally
        {
            server?.Dispose();
        }
    }

    private static SessionSummary ResolveSession(IReadOnlyList<SessionSummary> sessions, string name, string? workdir)
    {
        var matches = sessions.Where(session => string.Equals(session.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (workdir != null)
        {
            var normalized = SessionStore.NormalizeWorkdir(workdir);
            matches = matches.Where(session => string.Equals(session.Workdir, normalized, PlatformServices.Current.Paths.Comparison)).ToList();
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count == 0)
        {
            throw new TuiException(3, $"No session named \"{name}\" exists{(workdir != null ? $" for {workdir}" : string.Empty)}.");
        }

        throw new TuiException(3, $"Several sessions are named \"{name}\": {string.Join("; ", matches.Select(session => session.Workdir))}. Pass --workdir to pick one.");
    }

    private static IReadOnlyList<TranscriptEntry> LoadEntries(string directory)
    {
        var text = SharedFile.TryReadAllText(Path.Combine(directory, "session.json"));

        if (text == null)
        {
            return [];
        }

        SessionRecord? record;

        try
        {
            record = JsonSerializer.Deserialize<SessionRecord>(text, SessionStore.RecordJsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        if (record == null)
        {
            return [];
        }

        var tail = new TranscriptTail(Path.Combine(directory, "transcript.jsonl"));
        return tail.Refresh(record).Entries;
    }

    private static int? ResolveContextWindow(SubordinateConfig? config, string alias)
    {
        return config != null && config.Models.TryGetValue(alias, out var model) ? model.ContextWindow : null;
    }
}

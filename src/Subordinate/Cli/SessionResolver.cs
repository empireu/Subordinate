using System.Text.Json;
using Subordinate.Core;
using Subordinate.Core.Output;
using Subordinate.Core.Sessions;

namespace Subordinate.Cli;

/// <summary>
///     Finds sessions by (workdir, name), and reads their live state.
/// </summary>
internal static class SessionResolver
{
    /// <summary>
    ///     Resolves a session. With a workdir, the pair must match exactly. Without one, the current directory is tried first, then the name alone across all workdirs.
    /// </summary>
    public static (SessionPaths Paths, SessionRecord Record) Resolve(SessionStore store, string name, string? workdir)
    {
        SessionStore.ValidateName(name);

        if (workdir != null)
        {
            var normalized = SessionStore.NormalizeWorkdir(workdir);
            var exact = store.PathsFor(normalized, name);

            if (!File.Exists(exact.RecordFile))
            {
                throw new SubordinateException(ErrorCode.UnknownSession, $"No session named \"{name}\" exists for {normalized}. Run 'subordinate list' to see sessions.");
            }

            return (exact, store.Load(exact));
        }

        var current = store.PathsFor(Directory.GetCurrentDirectory(), name);

        if (File.Exists(current.RecordFile))
        {
            return (current, store.Load(current));
        }

        var matches = new List<(SessionPaths Paths, SessionRecord Record)>();

        foreach (var paths in store.Enumerate())
        {
            try
            {
                var record = store.Load(paths);

                if (string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add((paths, record));
                }
            }
            catch (SubordinateException)
            {
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new SubordinateException(ErrorCode.UnknownSession, $"No session named \"{name}\" exists. Run 'subordinate list' to see sessions."),
            _ => throw new SubordinateException(ErrorCode.AmbiguousSession, $"Several sessions are named \"{name}\": {string.Join("; ", matches.Select(match => match.Record.Workdir))}. Pass --workdir to pick one.")
        };
    }

    /// <summary>
    ///     The state to report. A session recorded as running whose lock is free was interrupted, for example by a crash.
    /// </summary>
    public static string LiveState(SessionPaths paths, SessionRecord record)
    {
        if (SessionStates.IsActive(record.State) && !SessionLock.IsHeld(paths))
        {
            return SessionStates.Interrupted;
        }

        return record.State;
    }

    public static string? CurrentRunDirectory(SessionPaths paths, SessionRecord record)
    {
        var run = record.CurrentRun;
        return run == null ? null : paths.RunDirectory(run.RunId);
    }

    public static JsonElement? ReadStatus(string? runDirectory)
    {
        if (runDirectory == null)
        {
            return null;
        }

        var file = RunArtifacts.StatusFile(runDirectory);

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

using System.Text.Json;
using Subordinate.Core.Platform;
using Subordinate.Core.Sessions;

namespace Subordinate.Tui.Data;

public sealed class RunSummary
{
    public required string RunId { get; init; }

    public required string Kind { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public required CountersRecord Counters { get; init; }
}

public sealed class SessionSummary
{
    public required string Directory { get; init; }

    public required string Name { get; init; }

    public required string Workdir { get; init; }

    public required string DisplayState { get; init; }

    public required string ModelAlias { get; init; }

    public required string ModelId { get; init; }

    public required IReadOnlyList<string> Policy { get; init; }

    public required string ShellKind { get; init; }

    public required int MaxTurns { get; init; }

    public required CountersRecord SessionCounters { get; init; }

    public RunSummary? CurrentRun { get; init; }

    public required int PendingCount { get; init; }

    public required int NeedsHumanCount { get; init; }

    public required long ContextTokens { get; init; }

    public string? ErrorMessage { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required DateTimeOffset SessionStartedAt { get; init; }

    public required bool HasFinalText { get; init; }

    public double? ElapsedSeconds { get; init; }

    public int? TurnsRemaining { get; init; }

    public string? LastActivity { get; init; }

    public int? ProcessId { get; init; }

    public DateTimeOffset? StatusUpdatedAt { get; init; }
}

public sealed class CatalogSnapshot
{
    public required IReadOnlyList<SessionSummary> Sessions { get; init; }

    public required long Version { get; init; }

    public required int UnreadableCount { get; init; }
}

public sealed class SessionCatalog
{
    private static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(15);

    private readonly string _sessionsDirectory;
    private readonly Func<string, bool>? _isLive;
    private readonly Dictionary<string, CachedSession> _cache = new(PlatformServices.Current.Paths.Comparer);

    private CatalogSnapshot _snapshot = new() { Sessions = [], Version = 0, UnreadableCount = 0 };
    private long _version;
    private int _recordReads;

    public SessionCatalog(string home, Func<string, bool>? isLive = null)
    {
        _sessionsDirectory = Path.Combine(home, "sessions");
        _isLive = isLive;
    }

    public CatalogSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal int RecordReadCount => Volatile.Read(ref _recordReads);

    internal int CachedSessionCount
    {
        get
        {
            lock (_cache)
            {
                return _cache.Count;
            }
        }
    }

    public void PollOnce()
    {
        var summaries = new List<SessionSummary>();
        var unreadable = 0;
        var seen = new HashSet<string>(PlatformServices.Current.Paths.Comparer);

        if (Directory.Exists(_sessionsDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(_sessionsDirectory))
            {
                var fullDirectory = Path.GetFullPath(directory);
                seen.Add(fullDirectory);
                var summary = PollSession(fullDirectory, ref unreadable);

                if (summary != null)
                {
                    summaries.Add(summary);
                }
            }
        }

        lock (_cache)
        {
            foreach (var key in _cache.Keys.Where(key => !seen.Contains(key)).ToList())
            {
                _cache.Remove(key);
            }
        }

        summaries.Sort((left, right) => PlatformServices.Current.Paths.Comparer.Compare(left.Directory, right.Directory));
        var snapshot = new CatalogSnapshot
        {
            Sessions = summaries,
            Version = Interlocked.Increment(ref _version),
            UnreadableCount = unreadable
        };
        Volatile.Write(ref _snapshot, snapshot);
    }

    private SessionSummary? PollSession(string directory, ref int unreadable)
    {
        var recordFile = Path.Combine(directory, "session.json");
        FileInfo recordInfo;

        try
        {
            recordInfo = new FileInfo(recordFile);

            if (!recordInfo.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable++;
            return null;
        }

        CachedSession? cached;
        lock (_cache)
        {
            _cache.TryGetValue(directory, out cached);
        }

        SessionRecord? record;

        if (cached != null && cached.RecordWriteTimeUtc == recordInfo.LastWriteTimeUtc && cached.RecordLength == recordInfo.Length)
        {
            record = cached.Record;
        }
        else
        {
            var text = SharedFile.TryReadAllText(recordFile);

            if (text == null)
            {
                unreadable++;
                return null;
            }

            try
            {
                record = JsonSerializer.Deserialize<SessionRecord>(text, SessionStore.RecordJsonOptions);
            }
            catch (JsonException)
            {
                unreadable++;
                return null;
            }

            if (record == null || record.Schema != 1)
            {
                unreadable++;
                return null;
            }

            Interlocked.Increment(ref _recordReads);
            cached = new CachedSession
            {
                Record = record,
                RecordWriteTimeUtc = recordInfo.LastWriteTimeUtc,
                RecordLength = recordInfo.Length
            };
            lock (_cache)
            {
                _cache[directory] = cached;
            }
        }

        StatusFile? status = null;

        if (SessionStates.IsActive(record.State) && record.CurrentRun is { } run)
        {
            var statusFile = Path.Combine(directory, "runs", run.RunId, "status.json");
            status = ReadStatus(statusFile, cached);
        }

        return BuildSummary(directory, record, status);
    }

    private static StatusFile? ReadStatus(string path, CachedSession cached)
    {
        FileInfo info;

        try
        {
            info = new FileInfo(path);

            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (cached.Status != null && cached.StatusWriteTimeUtc == info.LastWriteTimeUtc && cached.StatusLength == info.Length)
        {
            return cached.Status;
        }

        var text = SharedFile.TryReadAllText(path);

        if (text == null)
        {
            return null;
        }

        try
        {
            var status = JsonSerializer.Deserialize<StatusFile>(text, SessionStore.RecordJsonOptions);
            cached.Status = status;
            cached.StatusWriteTimeUtc = info.LastWriteTimeUtc;
            cached.StatusLength = info.Length;
            return status;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SessionSummary BuildSummary(string directory, SessionRecord record, StatusFile? status)
    {
        var currentRun = record.CurrentRun == null
            ? null
            : new RunSummary
            {
                RunId = record.CurrentRun.RunId,
                Kind = record.CurrentRun.Kind,
                StartedAt = record.CurrentRun.StartedAt,
                EndedAt = record.CurrentRun.EndedAt,
                Counters = record.CurrentRun.Counters
            };

        return new SessionSummary
        {
            Directory = directory,
            Name = record.Name,
            Workdir = record.Workdir,
            DisplayState = ResolveDisplayState(directory, record, status),
            ModelAlias = record.Model.Alias,
            ModelId = record.Model.ModelId,
            Policy = record.Policy.ToArray(),
            ShellKind = record.Shell.Kind,
            MaxTurns = record.Limits.MaxTurns,
            SessionCounters = record.Counters,
            CurrentRun = currentRun,
            PendingCount = record.PendingCount,
            NeedsHumanCount = record.Pending.Count(item => item.Status == PendingStatuses.Pending && item.NeedsHuman),
            ContextTokens = record.ContextTokens,
            ErrorMessage = record.Error?.Message,
            UpdatedAt = record.UpdatedAt,
            SessionStartedAt = record.Runs.Count > 0 ? record.Runs[0].StartedAt : record.CreatedAt,
            HasFinalText = record.FinalText != null,
            ElapsedSeconds = status?.ElapsedSeconds,
            TurnsRemaining = status?.TurnsRemaining,
            LastActivity = status?.LastActivity,
            ProcessId = status?.Pid,
            StatusUpdatedAt = status?.UpdatedAt
        };
    }

    private string ResolveDisplayState(string directory, SessionRecord record, StatusFile? status)
    {
        if (SessionStates.IsActive(record.State))
        {
            var now = DateTimeOffset.UtcNow;
            var statusFresh = status?.UpdatedAt is { } statusTime && now - statusTime <= LivenessWindow;
            var recordFresh = now - record.UpdatedAt <= LivenessWindow;
            var live = _isLive?.Invoke(directory) == true;
            return statusFresh || recordFresh || live ? SessionStates.Running : SessionStates.Interrupted;
        }

        return record.State switch
        {
            SessionStates.AwaitingApproval => "approval",
            SessionStates.LimitReached => "limit",
            SessionStates.Stopped => "stopped",
            SessionStates.Failed => "failed",
            SessionStates.Finished => "finished",
            SessionStates.Interrupted => "interrupted",
            _ => record.State
        };
    }

    private sealed class CachedSession
    {
        public required SessionRecord Record { get; init; }

        public required DateTime RecordWriteTimeUtc { get; init; }

        public required long RecordLength { get; init; }

        public StatusFile? Status { get; set; }

        public DateTime StatusWriteTimeUtc { get; set; }

        public long StatusLength { get; set; }
    }

    private sealed class StatusFile
    {
        public string State { get; set; } = string.Empty;

        public double ElapsedSeconds { get; set; }

        public int TurnsRemaining { get; set; }

        public int PendingCount { get; set; }

        public string LastActivity { get; set; } = string.Empty;

        public int Pid { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}

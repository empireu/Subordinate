using Subordinate.Core.Monitoring;
using Subordinate.Core.Platform;

namespace Subordinate.Tui.Live;

public sealed record LiveSessionSnapshot(MonitorSnapshot State, bool IsConnected, DateTimeOffset LastMessageAt, string? EndedState, string? ErrorMessage);

public sealed class LiveSessionRegistry
{
    private static readonly TimeSpan DisconnectedRetention = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(PlatformServices.Current.Paths.Comparer);

    private long _version;
    private int _helloCount;

    public long Version => Volatile.Read(ref _version);

    public int HelloCount => Volatile.Read(ref _helloCount);

    public void Apply(string directory, MonitorMessage message, bool connected)
    {
        var key = Path.GetFullPath(directory);

        lock (_lock)
        {
            if (message is MonitorHello)
            {
                _entries[key] = new Entry();
                Interlocked.Increment(ref _helloCount);
            }

            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            lock (entry.Lock)
            {
                entry.State.Apply(message);
                entry.IsConnected = connected;
                entry.LastMessageAt = DateTimeOffset.UtcNow;
            }

            Interlocked.Increment(ref _version);
        }
    }

    public void SetDisconnected(string directory)
    {
        var key = Path.GetFullPath(directory);

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                lock (entry.Lock)
                {
                    entry.IsConnected = false;
                    entry.LastMessageAt = DateTimeOffset.UtcNow;
                }
            }

            Interlocked.Increment(ref _version);
        }
    }

    public LiveSessionSnapshot? GetSnapshot(string directory)
    {
        return GetSnapshot(directory, MonitorProtocol.LiveTextTailCharacters);
    }

    /// <summary>
    ///     Copies only the last tailCharacters of the live reasoning and text, which is all a screen can show.
    /// </summary>
    public LiveSessionSnapshot? GetSnapshot(string directory, int tailCharacters)
    {
        var key = Path.GetFullPath(directory);

        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            lock (entry.Lock)
            {
                return new LiveSessionSnapshot(entry.State.CreateSnapshot(tailCharacters), entry.IsConnected, entry.LastMessageAt, entry.State.EndedState, entry.State.ErrorMessage);
            }
        }
    }

    public bool IsConnected(string directory)
    {
        var key = Path.GetFullPath(directory);

        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            lock (entry.Lock)
            {
                return entry.IsConnected;
            }
        }
    }

    public void Cleanup(DateTimeOffset now)
    {
        lock (_lock)
        {
            foreach (var key in _entries.Keys.ToList())
            {
                var entry = _entries[key];
                var remove = false;

                lock (entry.Lock)
                {
                    remove = !entry.IsConnected && now - entry.LastMessageAt > DisconnectedRetention;
                }

                if (remove)
                {
                    _entries.Remove(key);
                }
            }
        }
    }

    private sealed class Entry
    {
        public object Lock { get; } = new();

        public LiveRunState State { get; } = new();

        public bool IsConnected { get; set; }

        public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
    }
}

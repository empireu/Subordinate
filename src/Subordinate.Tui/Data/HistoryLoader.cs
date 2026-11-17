using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Sessions;

namespace Subordinate.Tui.Data;

public sealed record HistorySnapshot(
    string? Directory,
    IReadOnlyList<ChatMessage> NewMessages,
    int TotalCount,
    long Version,
    string? Notice,
    bool Reset,
    bool Loading);

public sealed class HistoryLoader : IDisposable
{
    private readonly object _lock = new();

    private TranscriptTail? _tail;
    private string? _directory;
    private DateTime _recordWriteTimeUtc;
    private long _recordLength = -1;
    private int _recordCount;
    private long _version;
    private readonly List<ChatMessage> _pendingMessages = [];
    private string? _notice;
    private bool _reset;
    private bool _loading;
    private bool _refreshRunning;
    private bool _disposed;
    private Task? _refreshTask;

    /// <summary>
    ///     Returns the messages loaded since the previous call and forgets them. A reset means earlier messages must be discarded first.
    /// </summary>
    public HistorySnapshot TakeChanges()
    {
        lock (_lock)
        {
            var snapshot = new HistorySnapshot(_directory, _pendingMessages.ToArray(), _recordCount, _version, _notice, _reset, _loading);
            _pendingMessages.Clear();
            _reset = false;
            return snapshot;
        }
    }

    public void Open(string directory)
    {
        lock (_lock)
        {
            _directory = directory;
            _tail = new TranscriptTail(Path.Combine(directory, "transcript.jsonl"));
            _recordWriteTimeUtc = default;
            _recordLength = -1;
            _recordCount = 0;
            _pendingMessages.Clear();
            _notice = null;
            _reset = true;
            _loading = true;
            _version++;
        }

        RequestRefresh(null);
    }

    public void Close()
    {
        lock (_lock)
        {
            _directory = null;
            _tail = null;
            _recordWriteTimeUtc = default;
            _recordLength = -1;
            _recordCount = 0;
            _pendingMessages.Clear();
            _notice = null;
            _reset = false;
            _loading = false;
            _version++;
        }
    }

    public void RequestRefresh(int? liveTranscriptCount)
    {
        lock (_lock)
        {
            if (_disposed || _directory == null || _refreshRunning || !NeedsRefreshLocked(liveTranscriptCount))
            {
                return;
            }

            _refreshRunning = true;
            var directory = _directory;
            _refreshTask = Task.Run(() => RefreshAsync(directory));
        }
    }

    public void Dispose()
    {
        Task? task;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            task = _refreshTask;
        }

        try
        {
            task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
    }

    private bool NeedsRefreshLocked(int? liveTranscriptCount)
    {
        if (_loading)
        {
            return true;
        }

        if (liveTranscriptCount.HasValue && liveTranscriptCount.Value > _recordCount)
        {
            return true;
        }

        try
        {
            var info = new FileInfo(Path.Combine(_directory!, "session.json"));
            return info.Exists && (info.LastWriteTimeUtc != _recordWriteTimeUtc || info.Length != _recordLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task RefreshAsync(string directory)
    {
        try
        {
            await Task.Yield();
            var recordFile = Path.Combine(directory, "session.json");
            var text = SharedFile.TryReadAllText(recordFile);

            if (text == null)
            {
                PublishError("The session record could not be read.");
                return;
            }

            SessionRecord? record;

            try
            {
                record = JsonSerializer.Deserialize<SessionRecord>(text, SessionStore.RecordJsonOptions);
            }
            catch (JsonException)
            {
                record = null;
            }

            if (record == null || record.Schema != 1)
            {
                PublishError("The session record cannot be read.");
                return;
            }

            DateTime writeTime;
            long length;

            try
            {
                var info = new FileInfo(recordFile);
                writeTime = info.Exists ? info.LastWriteTimeUtc : default;
                length = info.Exists ? info.Length : -1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writeTime = default;
                length = -1;
            }

            lock (_lock)
            {
                if (_directory != directory || _tail == null)
                {
                    return;
                }

                var refresh = _tail.Refresh(record);
                var changed = refresh.Reset || refresh.Messages.Count > 0 || refresh.Notice != _notice || _loading;
                _recordCount = _tail.MessagesRead;
                _recordWriteTimeUtc = writeTime;
                _recordLength = length;

                if (refresh.Reset)
                {
                    _pendingMessages.Clear();
                    _reset = true;
                }

                _pendingMessages.AddRange(refresh.Messages);
                _notice = refresh.Notice;
                _loading = false;

                if (changed)
                {
                    _version++;
                }
            }
        }
        catch (Exception)
        {
            PublishError("The transcript could not be read.");
        }
        finally
        {
            lock (_lock)
            {
                _refreshRunning = false;
                _refreshTask = null;
            }
        }
    }

    private void PublishError(string notice)
    {
        lock (_lock)
        {
            if (_notice == notice && !_loading)
            {
                return;
            }

            _notice = notice;
            _loading = false;
            _version++;
        }
    }
}

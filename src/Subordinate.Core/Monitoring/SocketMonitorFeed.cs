using System.Net.Sockets;
using System.Text;

namespace Subordinate.Core.Monitoring;

public sealed class SocketMonitorFeed : IMonitorFeed
{
    private const int MaxQueuedMessages = 4096;
    private const int MaxQueuedCharacters = 262_144;

    private readonly string _socketPath;
    private readonly MonitorRunInfo _runInfo;
    private readonly TimeSpan _connectInterval;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _writeTimeout;
    private readonly object _lock = new();
    private readonly LiveRunState _state = new();
    private readonly List<MonitorMessage> _queue = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();

    private Task? _loop;
    private Socket? _socket;
    private bool _connected;
    private bool _resync;
    private bool _disposed;
    private bool _started;
    private int _queuedCharacters;
    private int _disconnectCount;

    public SocketMonitorFeed(string socketPath, MonitorRunInfo runInfo, TimeSpan? connectInterval = null, TimeSpan? flushInterval = null, TimeSpan? writeTimeout = null)
    {
        _socketPath = socketPath;
        _runInfo = runInfo;
        _connectInterval = connectInterval ?? TimeSpan.FromSeconds(2);
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(100);
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(2);
    }

    internal int QueuedMessageCount
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    internal bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _connected;
            }
        }
    }

    internal int DisconnectCount => Volatile.Read(ref _disconnectCount);

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _loop = Task.Run(RunAsync);
        }
    }

    public void Publish(MonitorMessage message)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _state.Apply(message);

            if (!_connected)
            {
                return;
            }

            EnqueueLocked(message);
        }

        Signal();
    }

    public void Dispose()
    {
        Task? loop;
        var shouldWait = false;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
            shouldWait = _connected && (_queue.Count > 0 || _resync);
        }

        try
        {
            if (shouldWait)
            {
                Signal();
                loop?.Wait(TimeSpan.FromMilliseconds(500));
            }

            _stop.Cancel();
            Signal();
            loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
        }

        lock (_lock)
        {
            _socket?.Dispose();
            _socket = null;
            _connected = false;
            _queue.Clear();
            _queuedCharacters = 0;
        }

        _signal.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket? socket;

            lock (_lock)
            {
                socket = _socket;
            }

            if (socket == null)
            {
                await ConnectIfListeningAsync();
                continue;
            }

            await WaitForWorkAsync();

            if (_stop.IsCancellationRequested)
            {
                break;
            }

            await FlushAsync();
        }
    }

    private async Task ConnectIfListeningAsync()
    {
        if (!File.Exists(_socketPath))
        {
            await DelayAsync(_connectInterval);
            return;
        }

        Socket? candidate = null;

        try
        {
            candidate = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await candidate.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), timeout.Token);
            var hello = MonitorProtocol.Serialize(CreateHello());
            var snapshot = MonitorProtocol.Serialize(_state.CreateSnapshot());
            var payload = Encoding.ASCII.GetBytes(hello + "\n" + snapshot + "\n");
            await candidate.SendAsync(payload, SocketFlags.None, timeout.Token);

            lock (_lock)
            {
                if (_disposed)
                {
                    candidate.Dispose();
                    return;
                }

                _socket = candidate;
                _connected = true;
                _queue.Clear();
                _queuedCharacters = 0;
                _resync = false;
            }

            candidate = null;
        }
        catch (Exception)
        {
            candidate?.Dispose();
            await DelayAsync(_connectInterval);
        }
    }

    private async Task WaitForWorkAsync()
    {
        try
        {
            await _signal.WaitAsync(_flushInterval, _stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FlushAsync()
    {
        byte[]? payload;
        Socket? socket;

        lock (_lock)
        {
            socket = _socket;

            if (socket == null)
            {
                return;
            }

            if (_resync)
            {
                _queue.Clear();
                _queuedCharacters = 0;
                _resync = false;
                _queue.Add(_state.CreateSnapshot());
            }

            if (_queue.Count == 0)
            {
                return;
            }

            var builder = new StringBuilder();

            foreach (var message in _queue)
            {
                builder.Append(MonitorProtocol.Serialize(message)).Append('\n');
            }

            _queue.Clear();
            _queuedCharacters = 0;
            payload = Encoding.ASCII.GetBytes(builder.ToString());
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(_writeTimeout);
            await socket.SendAsync(payload, SocketFlags.None, timeout.Token);
        }
        catch (Exception)
        {
            CloseConnection();
        }
    }

    private void CloseConnection()
    {
        Interlocked.Increment(ref _disconnectCount);

        lock (_lock)
        {
            _socket?.Dispose();
            _socket = null;
            _connected = false;
            _queue.Clear();
            _queuedCharacters = 0;
            _resync = false;
        }
    }

    private void EnqueueLocked(MonitorMessage message)
    {
        if (message is MonitorReasoningDelta reasoningDelta && reasoningDelta.Text.Length > MonitorProtocol.MaxDeltaCharacters)
        {
            foreach (var chunk in SplitDelta(reasoningDelta.Text))
            {
                EnqueueDeltaLocked(new MonitorReasoningDelta(chunk), chunk.Length);
            }

            return;
        }

        if (message is MonitorTextDelta textDelta && textDelta.Text.Length > MonitorProtocol.MaxDeltaCharacters)
        {
            foreach (var chunk in SplitDelta(textDelta.Text))
            {
                EnqueueDeltaLocked(new MonitorTextDelta(chunk), chunk.Length);
            }

            return;
        }

        if (message is MonitorStatus)
        {
            for (var index = _queue.Count - 1; index >= 0; index--)
            {
                if (_queue[index] is MonitorStatus)
                {
                    _queuedCharacters -= MessageSize(_queue[index]);
                    _queue.RemoveAt(index);
                }
            }
        }

        if (message is MonitorReasoningDelta or MonitorTextDelta)
        {
            EnqueueDeltaLocked(message, MessageSize(message));
            return;
        }

        _queue.Add(message);
        _queuedCharacters += MessageSize(message);
        CheckQueueLimitsLocked();
    }

    private void EnqueueDeltaLocked(MonitorMessage message, int size)
    {
        if (_queue.Count > 0)
        {
            var last = _queue[^1];

            if (last is MonitorReasoningDelta lastReasoning && message is MonitorReasoningDelta currentReasoning && lastReasoning.Text.Length + currentReasoning.Text.Length <= MonitorProtocol.MaxDeltaCharacters)
            {
                _queue[^1] = lastReasoning with { Text = lastReasoning.Text + currentReasoning.Text };
                _queuedCharacters += currentReasoning.Text.Length;
                CheckQueueLimitsLocked();
                return;
            }

            if (last is MonitorTextDelta lastText && message is MonitorTextDelta currentText && lastText.Text.Length + currentText.Text.Length <= MonitorProtocol.MaxDeltaCharacters)
            {
                _queue[^1] = lastText with { Text = lastText.Text + currentText.Text };
                _queuedCharacters += currentText.Text.Length;
                CheckQueueLimitsLocked();
                return;
            }
        }

        _queue.Add(message);
        _queuedCharacters += size;
        CheckQueueLimitsLocked();
    }

    private void CheckQueueLimitsLocked()
    {
        if (_queue.Count > MaxQueuedMessages || _queuedCharacters > MaxQueuedCharacters)
        {
            _queue.Clear();
            _queuedCharacters = 0;
            _resync = true;
        }
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task DelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private MonitorHello CreateHello()
    {
        return new MonitorHello(
            _runInfo.SessionDirectory,
            _runInfo.Session,
            _runInfo.Workdir,
            _runInfo.RunId,
            _runInfo.RunKind,
            _runInfo.ProcessId,
            _runInfo.ModelAlias,
            _runInfo.ModelId,
            _runInfo.StartedAt);
    }

    private static IEnumerable<string> SplitDelta(string text)
    {
        var start = 0;

        while (start < text.Length)
        {
            var length = Math.Min(MonitorProtocol.MaxDeltaCharacters, text.Length - start);

            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]) && char.IsLowSurrogate(text[start + length]))
            {
                length--;
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }

    private static int MessageSize(MonitorMessage message)
    {
        return message switch
        {
            MonitorReasoningDelta reasoningDelta => reasoningDelta.Text.Length,
            MonitorTextDelta textDelta => textDelta.Text.Length,
            _ => 256
        };
    }
}

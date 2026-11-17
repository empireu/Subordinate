using System.Net.Sockets;
using System.Text;
using Subordinate.Core.Monitoring;

namespace Subordinate.Tui.Live;

public sealed class MonitorServer : IDisposable
{
    private const int MaxConnections = 64;

    private readonly MonitorEndpoint _endpoint;
    private readonly LiveSessionRegistry _registry;
    private readonly TimeSpan _helloTimeout;
    private readonly object _lock = new();
    private readonly List<Socket> _connections = [];
    private readonly CancellationTokenSource _stop = new();

    private Socket? _listener;
    private Task? _acceptLoop;
    private string _status = "unavailable: not started";
    private bool _ownsSocket;
    private bool _disposed;

    public MonitorServer(MonitorEndpoint endpoint, LiveSessionRegistry registry, TimeSpan? helloTimeout = null)
    {
        _endpoint = endpoint;
        _registry = registry;
        _helloTimeout = helloTimeout ?? TimeSpan.FromSeconds(5);
    }

    public LiveSessionRegistry Registry => _registry;

    public string Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public int ConnectionCount
    {
        get
        {
            lock (_lock)
            {
                return _connections.Count;
            }
        }
    }

    public void Start()
    {
        if (!_endpoint.Enabled || _endpoint.SocketPath == null)
        {
            SetStatus(_endpoint.Problem ?? "disabled in config.json");
            return;
        }

        var directory = Path.GetDirectoryName(_endpoint.SocketPath);

        if (directory == null || !Directory.Exists(directory))
        {
            SetStatus($"unavailable: {directory ?? _endpoint.SocketPath} does not exist");
            return;
        }

        if (File.Exists(_endpoint.SocketPath))
        {
            if (CanConnect(_endpoint.SocketPath))
            {
                SetStatus("another monitor is running");
                return;
            }

            try
            {
                File.Delete(_endpoint.SocketPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"unavailable: {ex.Message}");
                return;
            }
        }

        try
        {
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(_endpoint.SocketPath));
            listener.Listen(16);

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(_endpoint.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            lock (_lock)
            {
                _listener = listener;
                _ownsSocket = true;
                _status = "listening, 0 connected";
            }

            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener));
        }
        catch (Exception ex)
        {
            SetStatus($"unavailable: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _stop.Cancel();
        }
        catch (Exception)
        {
        }

        lock (_lock)
        {
            _listener?.Dispose();
            _listener = null;

            foreach (var connection in _connections)
            {
                try
                {
                    connection.Dispose();
                }
                catch (Exception)
                {
                }
            }

            _connections.Clear();
        }

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }

        if (_ownsSocket && _endpoint.SocketPath != null)
        {
            try
            {
                File.Delete(_endpoint.SocketPath);
            }
            catch (Exception)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(Socket listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket connection;

            try
            {
                connection = await listener.AcceptAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                continue;
            }

            lock (_lock)
            {
                if (_connections.Count >= MaxConnections)
                {
                    connection.Dispose();
                    continue;
                }

                _connections.Add(connection);
                _status = $"listening, {_connections.Count} connected";
            }

            _ = Task.Run(() => HandleConnectionAsync(connection));
        }
    }

    private async Task HandleConnectionAsync(Socket connection)
    {
        string? directory = null;
        var helloReceived = false;

        try
        {
            using var stream = new NetworkStream(connection, false);
            var reader = new BoundedLineReader(stream);
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            helloTimeout.CancelAfter(_helloTimeout);

            while (!_stop.IsCancellationRequested)
            {
                string? line;

                try
                {
                    line = await reader.ReadLineAsync(helloReceived ? _stop.Token : helloTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (InvalidDataException)
                {
                    break;
                }

                if (line == null)
                {
                    break;
                }

                var result = MonitorProtocol.Parse(Encoding.UTF8.GetBytes(line));

                if (result.Outcome == MonitorParseOutcome.Invalid)
                {
                    break;
                }

                if (result.Outcome == MonitorParseOutcome.Ignored)
                {
                    continue;
                }

                if (!helloReceived)
                {
                    if (result.Message is not MonitorHello hello)
                    {
                        break;
                    }

                    helloReceived = true;
                    directory = hello.SessionDirectory;
                    _registry.Apply(directory, hello, true);
                    continue;
                }

                if (result.Message is MonitorHello nextHello)
                {
                    if (directory != null)
                    {
                        _registry.SetDisconnected(directory);
                    }

                    directory = nextHello.SessionDirectory;
                }

                if (directory != null)
                {
                    _registry.Apply(directory, result.Message!, true);
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            if (directory != null)
            {
                _registry.SetDisconnected(directory);
            }

            lock (_lock)
            {
                _connections.Remove(connection);
                _status = _listener == null ? _status : $"listening, {_connections.Count} connected";
            }

            connection.Dispose();
        }
    }

    private static bool CanConnect(string path)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SetStatus(string status)
    {
        lock (_lock)
        {
            _status = status;
        }
    }
}

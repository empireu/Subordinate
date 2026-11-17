using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Monitoring;
using Subordinate.Core.Output;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

[CollectionDefinition("monitor", DisableParallelization = true)]
public sealed class MonitorCollection
{
}

internal sealed class TestSocketPath : IDisposable
{
    public TestSocketPath()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class StreamingScriptedProvider(params IReadOnlyList<ChatStreamEvent>[] turns) : IChatProvider
{
    private readonly Queue<IReadOnlyList<ChatStreamEvent>> _turns = new(turns);

    public List<ChatRequest> Requests { get; } = [];

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource GateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ProviderId => "streaming-scripted";

    public string? DefaultModel => "streaming-scripted";

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (!_turns.TryDequeue(out var events))
        {
            yield return new StreamErrorEvent(new ChatError { Message = "The streaming scripted provider has no more turns." });
            yield break;
        }

        foreach (var streamEvent in events)
        {
            yield return streamEvent;

            if (Gate != null)
            {
                GateReached.TrySetResult();
                await Gate.Task.WaitAsync(cancellationToken);
            }
        }
    }

    public async Task<ChatMessage> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in StreamAsync(request, cancellationToken))
        {
            if (streamEvent is StreamCompletedEvent completed)
            {
                return completed.Message;
            }
        }

        throw new InvalidOperationException("The streaming scripted provider has no completed message.");
    }
}

internal sealed class RecordingMonitorFeed : IMonitorFeed
{
    private readonly object _lock = new();
    private readonly List<MonitorMessage> _messages = [];

    public IReadOnlyList<MonitorMessage> Messages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToArray();
            }
        }
    }

    public void Start()
    {
    }

    public void Publish(MonitorMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class ThrowingMonitorFeed : IMonitorFeed
{
    public void Start()
    {
        throw new InvalidOperationException("The feed failed to start.");
    }

    public void Publish(MonitorMessage message)
    {
        throw new InvalidOperationException("The feed failed to publish.");
    }

    public void Dispose()
    {
        throw new InvalidOperationException("The feed failed to dispose.");
    }
}

internal sealed class TestMonitorListener : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly List<Socket> _connections = [];
    private readonly object _lock = new();
    private volatile bool _reading = true;

    public TestMonitorListener(string path)
    {
        Path = path;
        File.Delete(path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(16);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string Path { get; }

    public IReadOnlyList<string> Lines => _lines.ToArray();

    public void StopReading()
    {
        _reading = false;
    }

    public async Task<string> WaitForLineAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            foreach (var line in _lines)
            {
                if (predicate(line))
                {
                    return line;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected monitor line did not arrive.");
    }

    public void Dispose()
    {
        _stop.Cancel();

        try
        {
            _listener.Dispose();
        }
        catch (Exception)
        {
        }

        lock (_lock)
        {
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
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }

        File.Delete(Path);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket connection;

            try
            {
                connection = await _listener.AcceptAsync(_stop.Token);
            }
            catch (Exception)
            {
                break;
            }

            lock (_lock)
            {
                _connections.Add(connection);
            }

            connection.ReceiveBufferSize = 4096;
            _ = Task.Run(() => ReadConnectionAsync(connection));
        }
    }

    private async Task ReadConnectionAsync(Socket connection)
    {
        try
        {
            using var stream = new NetworkStream(connection, false);
            using var reader = new StreamReader(stream, Encoding.ASCII);

            while (!_stop.IsCancellationRequested)
            {
                if (!_reading)
                {
                    await Task.Delay(50, _stop.Token);
                    continue;
                }

                var line = await reader.ReadLineAsync(_stop.Token);

                if (line == null)
                {
                    break;
                }

                _lines.Enqueue(line);
            }
        }
        catch (Exception)
        {
        }
    }
}

internal static class MonitorTestWait
{
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(message);
    }
}

internal sealed class TuiTestHome : IDisposable
{
    public TuiTestHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        Workdir = Path.Combine(Root, "work");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Workdir);
        Store = new SessionStore(Home);
    }

    public string Root { get; }

    public string Home { get; }

    public string Workdir { get; }

    public SessionStore Store { get; }

    public SessionPaths CreateSession(string name, string state, DateTimeOffset updatedAt, string task = "Do the task.", long contextTokens = 0)
    {
        var record = NewRecord(name, state, updatedAt, contextTokens);
        var paths = Store.Create(record);
        record.Transcript = Store.AppendTranscript(paths, record.Transcript,
        [
            new ChatMessage { Role = ChatRole.System, Contents = [new TextContent { Text = "You are a test subagent." }] },
            new ChatMessage { Role = ChatRole.User, Contents = [new TextContent { Text = task }] }
        ]);
        record.UpdatedAt = updatedAt;
        File.WriteAllText(paths.RecordFile, JsonSerializer.Serialize(record, SessionStore.RecordJsonOptions));
        return paths;
    }

    public SessionPaths CreateRunningSession(string name, DateTimeOffset startedAt, long contextTokens = 0)
    {
        var record = NewRecord(name, SessionStates.Running, DateTimeOffset.UtcNow, contextTokens);
        var paths = Store.Create(record);
        var runId = Store.CreateRunDirectory(paths, 1, 10);
        record.Runs.Add(new RunRecord { RunId = runId, Kind = "start", StartedAt = startedAt, State = SessionStates.Running });
        record.Transcript = Store.AppendTranscript(paths, record.Transcript,
        [
            new ChatMessage { Role = ChatRole.System, Contents = [new TextContent { Text = "You are a test subagent." }] },
            new ChatMessage { Role = ChatRole.User, Contents = [new TextContent { Text = "Do the running task." }] }
        ]);
        record.UpdatedAt = DateTimeOffset.UtcNow;
        File.WriteAllText(paths.RecordFile, JsonSerializer.Serialize(record, SessionStore.RecordJsonOptions));
        RunArtifacts.WriteStatus(paths.RunDirectory(runId), new StatusSnapshot
        {
            Session = record.Name,
            Workdir = record.Workdir,
            RunId = runId,
            State = SessionStates.Running,
            ProcessId = 1234,
            StartedAt = startedAt,
            ElapsedSeconds = 12.5,
            PendingCount = 0,
            RunCounters = record.CurrentRun!.Counters,
            SessionCounters = record.Counters,
            TurnsRemaining = 5,
            LastActivity = "waiting for the model",
            ContextTokens = contextTokens
        });
        return paths;
    }

    public void AppendMessages(SessionPaths paths, params ChatMessage[] messages)
    {
        var record = Store.Load(paths);
        record.Transcript = Store.AppendTranscript(paths, record.Transcript, messages);
        Store.Save(paths, record);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private SessionRecord NewRecord(string name, string state, DateTimeOffset updatedAt, long contextTokens)
    {
        return new SessionRecord
        {
            Name = name,
            Workdir = Workdir,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            Shell = new ShellRecord { Kind = "bash", Dialect = "bash", ExecutablePath = "bash.exe", Version = "5.2.26" },
            Model = new ModelRecord { Alias = "test", Provider = "test", ModelId = "scripted" },
            Policy = ["readonly"],
            Limits = new LimitsRecord { MaxTurns = 20, MaxMinutes = 5, ShellTimeoutSeconds = 30 },
            State = state,
            ContextTokens = contextTokens
        };
    }
}


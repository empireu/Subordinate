using System.Net.Sockets;
using System.Text;
using Subordinate.Core.Monitoring;
using Subordinate.Tui.Live;

namespace Subordinate.Tests;

[Collection("monitor")]
public class MonitorServerTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly string SessionDirectory = Path.Combine(Path.GetTempPath(), "sm-monitor-session");

    [Fact]
    public async Task ClientMessagesReachTheRegistry()
    {
        using var path = new TestSocketPath();
        var registry = new LiveSessionRegistry();
        using var server = new MonitorServer(Endpoint(path.Path), registry, TimeSpan.FromSeconds(5));
        server.Start();
        Assert.Equal("listening, 0 connected", server.Status);

        using var client = await ConnectAsync(path.Path);
        await SendAsync(client, Hello());
        await SendAsync(client, new MonitorTurnStarted(1, Time));
        await SendAsync(client, new MonitorReasoningDelta("thinking"));
        await MonitorTestWait.WaitUntilAsync(() => registry.GetSnapshot(SessionDirectory)?.State.Reasoning == "thinking", TimeSpan.FromSeconds(5), "The registry did not receive the messages.");

        var snapshot = registry.GetSnapshot(SessionDirectory)!;
        Assert.True(snapshot.IsConnected);
        Assert.Equal("reasoning", snapshot.State.Phase);
    }

    [Fact]
    public async Task AGarbageLineClosesOnlyThatConnection()
    {
        using var path = new TestSocketPath();
        var registry = new LiveSessionRegistry();
        using var server = new MonitorServer(Endpoint(path.Path), registry, TimeSpan.FromSeconds(5));
        server.Start();

        using (var client = await ConnectAsync(path.Path))
        {
            await SendAsync(client, Hello());
            await SendRawAsync(client, "not json\n");
            await MonitorTestWait.WaitUntilAsync(() => server.ConnectionCount == 0, TimeSpan.FromSeconds(5), "The garbage connection stayed open.");
        }

        using var second = await ConnectAsync(path.Path);
        await SendAsync(second, Hello());
        await MonitorTestWait.WaitUntilAsync(() => registry.GetSnapshot(SessionDirectory)?.IsConnected == true, TimeSpan.FromSeconds(5), "The second connection did not work.");
    }

    [Fact]
    public async Task AConnectionWithoutHelloIsClosed()
    {
        using var path = new TestSocketPath();
        var registry = new LiveSessionRegistry();
        using var server = new MonitorServer(Endpoint(path.Path), registry, TimeSpan.FromMilliseconds(100));
        server.Start();

        using var client = await ConnectAsync(path.Path);
        await MonitorTestWait.WaitUntilAsync(() => server.ConnectionCount == 0, TimeSpan.FromSeconds(5), "The connection without hello stayed open.");
    }

    [Fact]
    public async Task TheSixtyFifthConnectionIsClosed()
    {
        using var path = new TestSocketPath();
        var registry = new LiveSessionRegistry();
        using var server = new MonitorServer(Endpoint(path.Path), registry, TimeSpan.FromSeconds(30));
        server.Start();
        var clients = new List<Socket>();

        try
        {
            for (var index = 0; index < 65; index++)
            {
                clients.Add(await ConnectAsync(path.Path));
            }

            await MonitorTestWait.WaitUntilAsync(() => server.ConnectionCount == 64, TimeSpan.FromSeconds(5), "The server did not cap the connections.");
            Assert.Equal(64, server.ConnectionCount);
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public void AStaleFileAtTheSocketPathIsReplaced()
    {
        using var path = new TestSocketPath();
        File.WriteAllText(path.Path, "stale");
        var registry = new LiveSessionRegistry();
        using var server = new MonitorServer(Endpoint(path.Path), registry);
        server.Start();

        Assert.StartsWith("listening", server.Status);
    }

    [Fact]
    public async Task ASecondServerReportsBusyAndLeavesTheFirstWorking()
    {
        using var path = new TestSocketPath();
        var firstRegistry = new LiveSessionRegistry();
        using var first = new MonitorServer(Endpoint(path.Path), firstRegistry);
        first.Start();
        var secondRegistry = new LiveSessionRegistry();
        using var second = new MonitorServer(Endpoint(path.Path), secondRegistry);
        second.Start();

        Assert.Equal("another monitor is running", second.Status);

        using var client = await ConnectAsync(path.Path);
        await SendAsync(client, Hello());
        await MonitorTestWait.WaitUntilAsync(() => firstRegistry.GetSnapshot(SessionDirectory) != null, TimeSpan.FromSeconds(5), "The first server stopped working.");
    }

    [Fact]
    public void DisposeRemovesTheSocketFile()
    {
        var path = new TestSocketPath();
        var server = new MonitorServer(Endpoint(path.Path), new LiveSessionRegistry());
        server.Start();
        Assert.True(File.Exists(path.Path));

        server.Dispose();

        Assert.False(File.Exists(path.Path));
        path.Dispose();
    }

    [LinuxFact]
    public void TheSocketFileIsUserReadWriteOnly()
    {
        using var path = new TestSocketPath();
        using var server = new MonitorServer(Endpoint(path.Path), new LiveSessionRegistry());
        server.Start();

        var mode = File.GetUnixFileMode(path.Path);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void ADisabledEndpointDoesNotStart()
    {
        using var path = new TestSocketPath();
        using var server = new MonitorServer(new MonitorEndpoint(false, null, null), new LiveSessionRegistry());
        server.Start();

        Assert.Equal("disabled in config.json", server.Status);
        Assert.False(File.Exists(path.Path));
    }

    [Fact]
    public void AnEndpointProblemIsReported()
    {
        using var server = new MonitorServer(new MonitorEndpoint(false, null, "monitor.socket_path must be an absolute path."), new LiveSessionRegistry());
        server.Start();

        Assert.Equal("monitor.socket_path must be an absolute path.", server.Status);
    }

    private static MonitorEndpoint Endpoint(string path)
    {
        return new MonitorEndpoint(true, path, null);
    }

    private static MonitorHello Hello()
    {
        return new MonitorHello(SessionDirectory, "test", "/work", "0001-20261117-101500", "start", 1234, "alias", "model-id", Time);
    }

    private static async Task<Socket> ConnectAsync(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
        return socket;
    }

    private static async Task SendAsync(Socket socket, MonitorMessage message)
    {
        await SendRawAsync(socket, MonitorProtocol.Serialize(message) + "\n");
    }

    private static async Task SendRawAsync(Socket socket, string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line);
        await socket.SendAsync(bytes, SocketFlags.None);
    }
}

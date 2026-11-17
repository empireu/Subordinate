using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Monitoring;

namespace Subordinate.Tests;

[Collection("monitor")]
public class MonitorFeedTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void FactoryReturnsNullFeedWhenDisabledOrBroken()
    {
        using var path = new TestSocketPath();
        var info = RunInfo();

        Assert.Same(NullMonitorFeed.Instance, MonitorFeedFactory.Create(new MonitorEndpoint(false, null, null), info));
        Assert.Same(NullMonitorFeed.Instance, MonitorFeedFactory.Create(new MonitorEndpoint(false, null, "problem"), info));

        var feed = MonitorFeedFactory.Create(new MonitorEndpoint(true, path.Path, null), info);
        Assert.IsType<GuardedMonitorFeed>(feed);
        feed.Dispose();
    }

    [Fact]
    public void PublishingWithoutAListenerIsFastAndQueuesNothing()
    {
        using var path = new TestSocketPath();
        using var feed = new SocketMonitorFeed(path.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200));
        feed.Start();
        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < 100_000; index++)
        {
            feed.Publish(new MonitorReasoningDelta("a"));
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Publishing took {stopwatch.Elapsed}.");
        Assert.Equal(0, feed.QueuedMessageCount);

        var disposeWatch = Stopwatch.StartNew();
        feed.Dispose();
        disposeWatch.Stop();
        Assert.True(disposeWatch.Elapsed < TimeSpan.FromSeconds(1), $"Dispose took {disposeWatch.Elapsed}.");
    }

    [Fact]
    public async Task SendsHelloSnapshotAndEventsInOrder()
    {
        using var path = new TestSocketPath();
        using var listener = new TestMonitorListener(path.Path);
        using var feed = new SocketMonitorFeed(path.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        feed.Start();
        await MonitorTestWait.WaitUntilAsync(() => feed.IsConnected, TimeSpan.FromSeconds(5), "The feed did not connect.");

        feed.Publish(new MonitorTurnStarted(1, Time));
        feed.Publish(new MonitorReasoningDelta("abc"));

        var hello = await listener.WaitForLineAsync(line => line.Contains("\"type\":\"hello\""), TimeSpan.FromSeconds(5));
        var snapshot = await listener.WaitForLineAsync(line => line.Contains("\"type\":\"snapshot\""), TimeSpan.FromSeconds(5));
        await listener.WaitForLineAsync(line => line.Contains("\"type\":\"turn_started\""), TimeSpan.FromSeconds(5));
        await listener.WaitForLineAsync(line => line.Contains("\"type\":\"reasoning_delta\""), TimeSpan.FromSeconds(5));
        var lines = listener.Lines.ToList();

        Assert.True(lines.IndexOf(hello) < lines.IndexOf(snapshot), "The hello line did not come before the snapshot.");
        var reasoning = string.Concat(lines.Where(line => line.Contains("\"type\":\"reasoning_delta\"")).Select(ParseText));
        Assert.Equal("abc", reasoning);
    }

    [Fact]
    public async Task SnapshotCarriesStatePublishedBeforeTheListenerStarts()
    {
        using var path = new TestSocketPath();
        using var feed = new SocketMonitorFeed(path.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        feed.Start();
        feed.Publish(new MonitorTurnStarted(1, Time));
        feed.Publish(new MonitorReasoningDelta("abc"));

        using var listener = new TestMonitorListener(path.Path);
        var snapshotLine = await listener.WaitForLineAsync(line => line.Contains("\"type\":\"snapshot\""), TimeSpan.FromSeconds(5));
        using var document = JsonDocument.Parse(snapshotLine);

        Assert.Equal("abc", document.RootElement.GetProperty("reasoning").GetString());
        Assert.Equal("reasoning", document.RootElement.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task ReconnectsAfterTheListenerRestarts()
    {
        using var path = new TestSocketPath();
        var first = new TestMonitorListener(path.Path);
        using var feed = new SocketMonitorFeed(path.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        feed.Start();
        await MonitorTestWait.WaitUntilAsync(() => feed.IsConnected, TimeSpan.FromSeconds(5), "The feed did not connect.");
        feed.Publish(new MonitorReasoningDelta("first"));
        await first.WaitForLineAsync(line => line.Contains("\"type\":\"reasoning_delta\""), TimeSpan.FromSeconds(5));
        first.Dispose();

        using var second = new TestMonitorListener(path.Path);

        for (var index = 0; index < 10; index++)
        {
            feed.Publish(new MonitorReasoningDelta(new string('b', 16_000)));
        }

        await second.WaitForLineAsync(line => line.Contains("\"type\":\"hello\""), TimeSpan.FromSeconds(10));
        var snapshot = await second.WaitForLineAsync(line => line.Contains("\"type\":\"snapshot\""), TimeSpan.FromSeconds(10));
        Assert.Contains("bbbb", snapshot);
    }

    [Fact]
    public async Task AStuckListenerDoesNotBlockPublishing()
    {
        using var path = new TestSocketPath();
        using var listener = new TestMonitorListener(path.Path);
        listener.StopReading();
        using var feed = new SocketMonitorFeed(path.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200));
        feed.Start();
        await MonitorTestWait.WaitUntilAsync(() => feed.IsConnected, TimeSpan.FromSeconds(5), "The feed did not connect.");
        var stopwatch = Stopwatch.StartNew();
        var maximumQueued = 0;

        for (var index = 0; index < 3_200; index++)
        {
            feed.Publish(new MonitorReasoningDelta(new string('a', 16_000)));
            maximumQueued = Math.Max(maximumQueued, feed.QueuedMessageCount);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Publishing took {stopwatch.Elapsed}.");
        Assert.True(maximumQueued <= 4096, $"The queue grew to {maximumQueued} messages.");
        await MonitorTestWait.WaitUntilAsync(() => feed.DisconnectCount > 0, TimeSpan.FromSeconds(10), "The feed did not disconnect from the stuck listener.");
    }

    [Fact]
    public async Task LongDeltasAreSplitWithoutOversizedLines()
    {
        using var path = new TestSocketPath();
        using var listener = new TestMonitorListener(path.Path);
        using var feed = new SocketMonitorFeed(path.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        feed.Start();
        await MonitorTestWait.WaitUntilAsync(() => feed.IsConnected, TimeSpan.FromSeconds(5), "The feed did not connect.");
        var totalLength = MonitorProtocol.MaxDeltaCharacters * 3 + 10;

        feed.Publish(new MonitorReasoningDelta(new string('a', totalLength)));
        await listener.WaitForLineAsync(line => line.Contains("\"type\":\"reasoning_delta\""), TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        var deltas = listener.Lines.Where(line => line.Contains("\"type\":\"reasoning_delta\"")).ToList();
        Assert.True(deltas.Count >= 4, $"Only {deltas.Count} delta lines arrived.");
        Assert.All(deltas, line => Assert.True(Encoding.UTF8.GetByteCount(line) <= MonitorProtocol.MaxLineBytes));
        Assert.Equal(totalLength, string.Concat(deltas.Select(ParseText)).Length);
    }

    [Fact]
    public void GuardedFeedSwallowsEveryFailure()
    {
        var feed = new GuardedMonitorFeed(new ThrowingMonitorFeed());

        feed.Start();
        feed.Publish(new MonitorReasoningDelta("x"));
        feed.Dispose();
        feed.Dispose();
    }

    [Fact]
    public async Task MonitorCodeNeverWritesToTheConsole()
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var error = new StringWriter();

        try
        {
            Console.SetOut(output);
            Console.SetError(error);

            using (var harness = new SessionHarness())
            using (var path = new TestSocketPath())
            using (var feed = new SocketMonitorFeed(path.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200)))
            {
                var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
                await harness.RunAsync(provider, monitorFeed: feed);
            }

            using (var harness = new SessionHarness())
            using (var path = new TestSocketPath())
            using (var listener = new TestMonitorListener(path.Path))
            using (var feed = new SocketMonitorFeed(path.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200)))
            {
                var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
                await harness.RunAsync(provider, monitorFeed: feed);
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }

        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    private static MonitorRunInfo RunInfo()
    {
        return new MonitorRunInfo("/home/sessions/test", "test", "/work", "0001-20261117-101500", "start", 1234, "alias", "model-id", Time);
    }

    private static string ParseText(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }
}

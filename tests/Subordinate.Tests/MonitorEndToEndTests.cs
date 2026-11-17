using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Monitoring;
using Subordinate.Core.Sessions;
using Subordinate.Tui.Live;

namespace Subordinate.Tests;

[Collection("monitor")]
public class MonitorEndToEndTests
{
    [Fact]
    public async Task RegistryEndsWithTheFinishedRun()
    {
        using var path = new TestSocketPath();
        var registry = new LiveSessionRegistry();
        using var server = new MonitorServer(new MonitorEndpoint(true, path.Path, null), registry, TimeSpan.FromSeconds(5));
        server.Start();
        using var harness = new SessionHarness();
        var runInfo = new MonitorRunInfo(
            harness.Paths.Directory,
            "test",
            harness.Workdir,
            "0001-20261117-101500",
            "start",
            Environment.ProcessId,
            "test",
            "scripted",
            DateTimeOffset.UtcNow);
        var feed = new SocketMonitorFeed(path.Path, runInfo, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        var provider = new StreamingScriptedProvider(
            [new StreamCompletedEvent(ChatFinishReason.Stop, SessionHarness.Text(ChatRole.Assistant, "done"))]);

        var (outcome, record) = await harness.RunAsync(provider, monitorFeed: feed);

        Assert.Equal(SessionStates.Finished, outcome.State);
        await MonitorTestWait.WaitUntilAsync(
            () => registry.GetSnapshot(harness.Paths.Directory)?.State.Phase == "ended",
            TimeSpan.FromSeconds(5),
            "The registry did not see the end of the run.");
        var snapshot = registry.GetSnapshot(harness.Paths.Directory)!;
        Assert.Equal(SessionStates.Finished, snapshot.EndedState);
        Assert.Equal(record.Transcript.Count, snapshot.State.TranscriptCount);
    }
}

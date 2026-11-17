using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Cli;
using Subordinate.Core.Configuration;
using Subordinate.Core.Gate;
using Subordinate.Core.Monitoring;
using Subordinate.Core.Policy;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

[Collection("monitor")]
public class SessionRunnerMonitorTests
{
    [Fact]
    public async Task RunPublishesTheExpectedEventSequence()
    {
        using var harness = new SessionHarness();
        var feed = new RecordingMonitorFeed();
        var provider = CreateShellThenAnswerProvider("ls", "done");

        var (outcome, _) = await harness.RunAsync(provider, monitorFeed: feed);

        Assert.Equal(SessionStates.Finished, outcome.State);
        var messages = feed.Messages;
        Assert.Contains(messages, message => message is MonitorStatus);
        var ended = Assert.IsType<MonitorRunEnded>(messages[^1]);
        Assert.Equal(SessionStates.Finished, ended.State);
        AssertContainsInOrder(
            messages,
            message => message is MonitorTurnStarted,
            message => message is MonitorReasoningDelta delta && delta.Text == "thinking",
            message => message is MonitorToolCallProposed proposed && proposed.Tool == "shell" && proposed.Summary == "ls",
            message => message is MonitorTurnCompleted,
            message => message is MonitorToolStarted started && started.CallId == "call_1" && started.Tool == "shell" && started.Summary == "ls",
            message => message is MonitorToolFinished finished && finished.CallId == "call_1" && finished.Ok,
            message => message is MonitorTranscriptCommitted,
            message => message is MonitorTurnStarted,
            message => message is MonitorTextDelta delta && delta.Text == "done",
            message => message is MonitorTurnCompleted);
    }

    [Fact]
    public async Task DeferredCallPublishesAFailedToolFinish()
    {
        using var harness = new SessionHarness("readonly");
        var feed = new RecordingMonitorFeed();
        var provider = CreateWriteThenAnswerProvider(harness, "waiting");

        var (outcome, _) = await harness.RunAsync(provider, monitorFeed: feed);

        Assert.Equal(SessionStates.AwaitingApproval, outcome.State);
        var finished = Assert.Single(feed.Messages.OfType<MonitorToolFinished>());
        Assert.False(finished.Ok);
        Assert.StartsWith("PENDING APPROVAL", finished.FirstLine);
    }

    [Fact]
    public async Task ApprovedCallOnResumePublishesToolStartAndFinish()
    {
        using var harness = new SessionHarness("readonly");
        var firstProvider = CreateWriteThenAnswerProvider(harness, "waiting");
        var (firstOutcome, firstRecord) = await harness.RunAsync(firstProvider);
        Assert.Equal(SessionStates.AwaitingApproval, firstOutcome.State);
        Assert.Equal(1, firstRecord.PendingCount);

        var record = harness.Load();
        var ledger = new PendingLedger(record);
        var item = Assert.Single(record.Pending);
        ledger.Approve(item, ApprovalScope.Once, null);
        harness.Store.Save(harness.Paths, record);

        var feed = new RecordingMonitorFeed();
        var secondProvider = new StreamingScriptedProvider(
            [new StreamCompletedEvent(ChatFinishReason.Stop, SessionHarness.Text(ChatRole.Assistant, "done"))]);
        var (secondOutcome, _) = await harness.RunAsync(secondProvider, monitorFeed: feed);

        Assert.Equal(SessionStates.Finished, secondOutcome.State);
        var started = Assert.Single(feed.Messages.OfType<MonitorToolStarted>());
        Assert.Equal(item.CallId, started.CallId);
        Assert.Equal("write_file", started.Tool);
        Assert.Contains("write", started.Summary);
        var finished = Assert.Single(feed.Messages.OfType<MonitorToolFinished>());
        Assert.Equal(item.CallId, finished.CallId);
        Assert.True(finished.Ok);
    }

    [Fact]
    public async Task AThrowingFeedCannotFailTheRun()
    {
        using var harnessWithFeed = new SessionHarness();
        var providerWithFeed = CreateShellThenAnswerProvider("ls", "done");
        var (outcomeWithFeed, recordWithFeed) = await harnessWithFeed.RunAsync(providerWithFeed, monitorFeed: new ThrowingMonitorFeed());

        using var harnessWithoutFeed = new SessionHarness();
        var providerWithoutFeed = CreateShellThenAnswerProvider("ls", "done");
        var (outcomeWithoutFeed, recordWithoutFeed) = await harnessWithoutFeed.RunAsync(providerWithoutFeed);

        Assert.Equal(SessionStates.Finished, outcomeWithFeed.State);
        Assert.Equal(SessionStates.Finished, outcomeWithoutFeed.State);
        Assert.Equal(SessionHarness.Describe(harnessWithoutFeed.Transcript().Skip(1)), SessionHarness.Describe(harnessWithFeed.Transcript().Skip(1)));
        Assert.Equal(recordWithoutFeed.Counters.ModelCalls, recordWithFeed.Counters.ModelCalls);
        Assert.Equal(recordWithoutFeed.Counters.ToolCalls, recordWithFeed.Counters.ToolCalls);
        Assert.Equal(recordWithoutFeed.Counters.InputTokens, recordWithFeed.Counters.InputTokens);
        Assert.Equal(recordWithoutFeed.Counters.OutputTokens, recordWithFeed.Counters.OutputTokens);
    }

    [Fact]
    public async Task OutcomeIsIdenticalWithNoFeedSocketFeedAndListener()
    {
        var withoutFeed = await RunScript(null);
        using var pathWithoutListener = new TestSocketPath();
        var withoutListener = await RunScript(new SocketMonitorFeed(pathWithoutListener.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200)));
        using var pathWithListener = new TestSocketPath();
        using var listener = new TestMonitorListener(pathWithListener.Path);
        var withListener = await RunScript(new SocketMonitorFeed(pathWithListener.Path, RunInfo(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200)));

        Assert.Equal(withoutFeed.Transcript, withoutListener.Transcript);
        Assert.Equal(withoutFeed.Transcript, withListener.Transcript);
        Assert.Equal(withoutFeed.State, withoutListener.State);
        Assert.Equal(withoutFeed.State, withListener.State);
        Assert.Equal(withoutFeed.Counters, withoutListener.Counters);
        Assert.Equal(withoutFeed.Counters, withListener.Counters);
    }

    [Fact]
    public async Task LogMarkdownStillUsesTheToolSummary()
    {
        using var harness = new SessionHarness();
        var provider = CreateShellThenAnswerProvider("ls", "done");

        var (_, record) = await harness.RunAsync(provider);
        var log = File.ReadAllText(Path.Combine(harness.Paths.RunDirectory(record.CurrentRun!.RunId), "log.md"));

        Assert.Contains("] shell: ls", log);
        Assert.Contains("-> exit_code: 0", log);
    }

    [Fact]
    public void DoctorReportsTheMonitorAsEnabledByDefault()
    {
        using var harness = new ShortHome();
        harness.WriteConfig("""
            {
              "providers": { "test": { "endpoint": "https://example.invalid/v1", "api_key": "test" } },
              "models": { "fast": { "provider": "test", "model": "fast-model" } },
              "default_model": "fast"
            }
            """);

        var commandLine = CommandLine.Parse(["doctor"], new HashSet<string> { "model" }, new HashSet<string>());
        var result = UtilityCommands.Doctor(commandLine);
        var line = Assert.Single(result.Summary.Split('\n'), candidate => candidate.Contains("monitor", StringComparison.Ordinal));

        Assert.Contains("enabled", line);
        Assert.Contains("socket", line);
    }

    [Fact]
    public void DoctorReportsTheMonitorAsDisabled()
    {
        using var harness = new ShortHome();
        harness.WriteConfig("""
            {
              "providers": { "test": { "endpoint": "https://example.invalid/v1", "api_key": "test" } },
              "models": { "fast": { "provider": "test", "model": "fast-model" } },
              "default_model": "fast",
              "monitor": { "enabled": false }
            }
            """);

        var commandLine = CommandLine.Parse(["doctor"], new HashSet<string> { "model" }, new HashSet<string>());
        var result = UtilityCommands.Doctor(commandLine);
        var line = Assert.Single(result.Summary.Split('\n'), candidate => candidate.Contains("monitor", StringComparison.Ordinal));

        Assert.Contains("disabled in config.json", line);
    }

    private static async Task<(string Transcript, string State, string Counters)> RunScript(IMonitorFeed? feed)
    {
        using var harness = new SessionHarness();
        var provider = CreateShellThenAnswerProvider("ls", "done");
        var (outcome, record) = await harness.RunAsync(provider, monitorFeed: feed);
        return (SessionHarness.Describe(harness.Transcript().Skip(1)), outcome.State, $"{record.Counters.ModelCalls}/{record.Counters.ToolCalls}/{record.Counters.InputTokens}/{record.Counters.OutputTokens}");
    }

    private static StreamingScriptedProvider CreateShellThenAnswerProvider(string command, string answer)
    {
        var call = new ToolCallContent
        {
            CallId = "call_1",
            ToolName = "shell",
            Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["command"] = command })
        };
        var assistantWithCall = new ChatMessage { Role = ChatRole.Assistant, Contents = [call] };
        var final = SessionHarness.Text(ChatRole.Assistant, answer);

        return new StreamingScriptedProvider(
            [
                new ReasoningDeltaEvent(0, "thinking"),
                new ToolCallCompletedEvent(0, call),
                new UsageReceivedEvent(new ChatUsage { InputTokens = 100, OutputTokens = 10 }),
                new StreamCompletedEvent(ChatFinishReason.ToolCalls, assistantWithCall)
            ],
            [
                new TextDeltaEvent(0, answer),
                new UsageReceivedEvent(new ChatUsage { InputTokens = 200, OutputTokens = 20 }),
                new StreamCompletedEvent(ChatFinishReason.Stop, final)
            ]);
    }

    private static StreamingScriptedProvider CreateWriteThenAnswerProvider(SessionHarness harness, string answer)
    {
        var call = new ToolCallContent
        {
            CallId = "call_write",
            ToolName = "write_file",
            Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["path"] = Path.Combine(harness.Workdir, "approved.txt"),
                ["content"] = "hello"
            })
        };
        var assistantWithCall = new ChatMessage { Role = ChatRole.Assistant, Contents = [call] };

        return new StreamingScriptedProvider(
            [
                new ToolCallCompletedEvent(0, call),
                new StreamCompletedEvent(ChatFinishReason.ToolCalls, assistantWithCall)
            ],
            [
                new StreamCompletedEvent(ChatFinishReason.Stop, SessionHarness.Text(ChatRole.Assistant, answer))
            ]);
    }

    private static void AssertContainsInOrder(IReadOnlyList<MonitorMessage> messages, params Func<MonitorMessage, bool>[] predicates)
    {
        var index = 0;

        foreach (var message in messages)
        {
            if (index < predicates.Length && predicates[index](message))
            {
                index++;
            }
        }

        Assert.Equal(predicates.Length, index);
    }

    private static MonitorRunInfo RunInfo()
    {
        return new MonitorRunInfo("/home/sessions/test", "test", "/work", "0001-20261117-101500", "start", 1234, "alias", "model-id", DateTimeOffset.UtcNow);
    }

    private sealed class ShortHome : IDisposable
    {
        private readonly string? _previousHome;

        public ShortHome()
        {
            Home = Path.Combine(Path.GetTempPath(), "sm-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Home);
            _previousHome = Environment.GetEnvironmentVariable("SUBORDINATE_HOME");
            Environment.SetEnvironmentVariable("SUBORDINATE_HOME", Home);
        }

        public string Home { get; }

        public void WriteConfig(string json)
        {
            File.WriteAllText(Path.Combine(Home, SubordinateConfig.FileName), json);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SUBORDINATE_HOME", _previousHome);

            try
            {
                Directory.Delete(Home, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

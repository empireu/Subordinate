using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class SessionLifecycleRegressionTests
{
    [Fact]
    public async Task TurnLimitRequestsAFinalAnswerWithoutTools()
    {
        using var harness = new SessionHarness();
        var provider = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_1", "git status")),
            SessionHarness.Text(ChatRole.Assistant, "Final answer."));

        var (outcome, record) = await harness.RunAsync(provider, new RunLimits { MaxModelCalls = 1, MaxDuration = TimeSpan.FromMinutes(1) });

        Assert.True(outcome.State is SessionStates.Finished or SessionStates.LimitReached, outcome.State);
        Assert.Equal("Final answer.", record.FinalText);
        Assert.Equal(2, provider.FullRequests.Count);
        Assert.True(provider.FullRequests[1].ToolChoice == "none" || provider.FullRequests[1].Tools is null or { Count: 0 }, "The final turn must not offer tools.");
        Assert.Contains("Final answer.", File.ReadAllText(Path.Combine(outcome.RunDirectory, "result.md")));
    }

    [Fact]
    public async Task ToolCallsDuringTheForcedFinalTurnDoNotRun()
    {
        using var harness = new SessionHarness();
        var provider = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_1", "git status")),
            SessionHarness.ShellCalls(("call_2", "ls src")),
            SessionHarness.Text(ChatRole.Assistant, "Final answer."));

        var (outcome, _) = await harness.RunAsync(provider, new RunLimits { MaxModelCalls = 1, MaxDuration = TimeSpan.FromMinutes(1) });

        Assert.DoesNotContain(harness.Executor.Commands, command => command.Contains("ls src", StringComparison.Ordinal));
        Assert.Contains(SessionHarness.ToolResults(harness.Transcript()), result => result.CallId == "call_2" && result.Output.Contains("turn limit is reached", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(outcome.RunDirectory, "result.json")));
    }

    [Fact]
    public async Task AwaitingApprovalAfterAFinalAnswerIsMarkedInTheResult()
    {
        using var harness = new SessionHarness();
        var provider = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_1", "rm build.log")),
            SessionHarness.Text(ChatRole.Assistant, "I need approval to delete the log."));

        var (outcome, record) = await harness.RunAsync(provider);

        Assert.Equal(SessionStates.AwaitingApproval, outcome.State);
        Assert.Equal("I need approval to delete the log.", record.FinalText);
        var result = ReadJson(Path.Combine(outcome.RunDirectory, "result.json"));
        Assert.True(result.TryGetProperty("final_answer", out var finalAnswer), result.GetRawText());
        Assert.True(finalAnswer.GetBoolean());
    }

    [Fact]
    public async Task PendingFileExistsWhileTheRunWaitsForTheModel()
    {
        using var harness = new SessionHarness();
        var provider = new BlockingProvider(
            SessionHarness.ShellCalls(("call_1", "rm build.log")),
            SessionHarness.Text(ChatRole.Assistant, "Done."));

        var run = Task.Run(async () => await harness.RunAsync(provider));
        var pendingFileExists = false;

        try
        {
            await provider.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var record = harness.Load();
            var runDirectory = harness.Paths.RunDirectory(record.CurrentRun!.RunId);
            pendingFileExists = File.Exists(Path.Combine(runDirectory, "pending.json"));
        }
        finally
        {
            provider.Release.TrySetResult();
        }

        var (outcome, _) = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(pendingFileExists, "pending.json should exist while the run is waiting for the model.");
        Assert.Equal(SessionStates.AwaitingApproval, outcome.State);
    }

    [Fact]
    public async Task ResultReportsRunAndSessionCountersSeparately()
    {
        using var harness = new SessionHarness();
        var firstProvider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "First."))
        {
            Usage = new ChatUsage { InputTokens = 10, CachedInputTokens = 2, OutputTokens = 3 }
        };
        var (firstOutcome, _) = await harness.RunAsync(firstProvider);

        var secondProvider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "Second."))
        {
            Usage = new ChatUsage { InputTokens = 20, CachedInputTokens = 4, OutputTokens = 5 }
        };
        var (secondOutcome, _) = await harness.RunAsync(secondProvider);

        var first = ReadJson(Path.Combine(firstOutcome.RunDirectory, "result.json"));
        var second = ReadJson(Path.Combine(secondOutcome.RunDirectory, "result.json"));

        Assert.Equal(1, first.GetProperty("run").GetProperty("model_calls").GetInt32());
        Assert.Equal(1, first.GetProperty("session_totals").GetProperty("model_calls").GetInt32());
        Assert.Equal(10, first.GetProperty("run").GetProperty("input_tokens").GetInt64());
        Assert.Equal(10, first.GetProperty("session_totals").GetProperty("input_tokens").GetInt64());

        Assert.Equal(1, second.GetProperty("run").GetProperty("model_calls").GetInt32());
        Assert.Equal(2, second.GetProperty("session_totals").GetProperty("model_calls").GetInt32());
        Assert.Equal(20, second.GetProperty("run").GetProperty("input_tokens").GetInt64());
        Assert.Equal(30, second.GetProperty("session_totals").GetProperty("input_tokens").GetInt64());
    }

    [Fact]
    public async Task UsageLogSurvivesSessionDeletion()
    {
        using var harness = new SessionHarness();
        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "Done."))
        {
            Usage = new ChatUsage { InputTokens = 10, CachedInputTokens = 2, OutputTokens = 3 }
        };
        await harness.RunAsync(provider);

        var usageFile = Path.Combine(harness.Store.Home, "usage.jsonl");
        Assert.True(File.Exists(usageFile), "A usage log should be written when a run finishes.");
        var entry = Assert.Single(File.ReadAllLines(usageFile));

        using (var document = JsonDocument.Parse(entry))
        {
            var root = document.RootElement;
            Assert.Equal("test", root.GetProperty("session").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("run_id").GetString()));
            Assert.Equal(1, root.GetProperty("model_calls").GetInt32());
            Assert.Equal(10, root.GetProperty("input_tokens").GetInt64());
            Assert.Equal(3, root.GetProperty("output_tokens").GetInt64());
        }

        Directory.Delete(harness.Paths.Directory, true);

        Assert.True(File.Exists(usageFile));
        Assert.Equal(entry, Assert.Single(File.ReadAllLines(usageFile)));
    }

    [Fact]
    public async Task LongToolCallUpdatesStatusActivity()
    {
        using var harness = new SessionHarness();
        var executor = new BlockingExecutor();
        var provider = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_1", "git status")),
            SessionHarness.Text(ChatRole.Assistant, "Done."));

        var run = Task.Run(async () => await harness.RunAsync(provider, executor: executor));
        string? firstUpdatedAt = null;
        string? lastUpdatedAt = null;

        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var record = harness.Load();
            var runDirectory = harness.Paths.RunDirectory(record.CurrentRun!.RunId);
            var statusFile = Path.Combine(runDirectory, "status.json");
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(statusFile))
                    {
                        var status = ReadJson(statusFile);
                        lastUpdatedAt = status.GetProperty("updated_at").GetString();

                        if (firstUpdatedAt == null)
                        {
                            firstUpdatedAt = lastUpdatedAt;
                        }
                        else if (lastUpdatedAt != firstUpdatedAt)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                }

                await Task.Delay(100);
            }
        }
        finally
        {
            executor.Release.TrySetResult();
        }

        var (outcome, _) = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(firstUpdatedAt, lastUpdatedAt);
        Assert.Equal(SessionStates.Finished, outcome.State);
    }

    [Fact]
    public async Task ANewRunClearsThePreviousFinalAnswer()
    {
        using var harness = new SessionHarness();
        var firstProvider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "First answer."));
        var (_, firstRecord) = await harness.RunAsync(firstProvider);
        Assert.Equal("First answer.", firstRecord.FinalText);

        var executor = new BlockingExecutor();
        var secondProvider = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_1", "git status")),
            SessionHarness.Text(ChatRole.Assistant, "Second answer."));

        var run = Task.Run(async () => await harness.RunAsync(secondProvider, executor: executor));

        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(harness.Load().FinalText);
        }
        finally
        {
            executor.Release.TrySetResult();
        }

        var (_, secondRecord) = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Second answer.", secondRecord.FinalText);
    }

    [Fact]
    public async Task ResumeReportsAClosedInflightCallAsClosed()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript,
        [
            SessionHarness.ShellCalls(("call_1", "git status"))
        ]);
        record.Inflight = new InflightRecord
        {
            AssistantIndex = record.Transcript.Count - 1,
            Frames = [new InflightFrameRecord { CallId = "call_1", State = FrameStates.Deferred }]
        };
        record.Pending.Add(new PendingRecord
        {
            Id = "0123456789",
            CallId = "call_1",
            Tool = "shell",
            Status = PendingStatuses.Closed,
            DeferralMessage = "PENDING APPROVAL 0123456789: this call was not executed."
        });
        harness.Store.Save(harness.Paths, record);

        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "Final answer."));
        var (outcome, after) = await harness.RunAsync(provider);

        Assert.Equal(SessionStates.Finished, outcome.State);
        var result = Assert.Single(SessionHarness.ToolResults(provider.Requests[0]), item => item.CallId == "call_1");
        Assert.Contains("CLOSED 0123456789", result.Output);
        Assert.Contains("will not be decided", result.Output);
        Assert.Equal(PendingStatuses.Closed, Assert.Single(after.Pending).Status);
    }

    private static JsonElement ReadJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}

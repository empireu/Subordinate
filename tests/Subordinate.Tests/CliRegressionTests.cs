using System.Text.Json;
using Subordinate.Cli;
using Subordinate.Core;
using Subordinate.Core.Output;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

[Collection("cli")]
public class CliRegressionTests
{
    [Fact]
    public void StatusDoesNotAdvertiseAMissingPendingFile()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "status-test", workdir, SessionStates.AwaitingApproval);
        var paths = store.PathsFor(workdir, "status-test");
        var runId = store.CreateRunDirectory(paths, 1, 10);
        var record = store.Load(paths);
        record.Runs.Add(new RunRecord { RunId = runId, Kind = "start", StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, State = SessionStates.AwaitingApproval });
        record.Pending.Add(new PendingRecord { Id = "0123456789", CallId = "call_1", Tool = "shell", Summary = "rm build.log", DeferralMessage = "PENDING APPROVAL 0123456789: rm build.log" });
        store.Save(paths, record);

        var commandLine = CommandLine.Parse(["status", "--name", "status-test"], new HashSet<string> { "name", "workdir" }, new HashSet<string>());
        var result = SessionCommands.Status(commandLine);

        using var document = JsonDocument.Parse(result.Envelope.ToJson());
        Assert.Equal(1, document.RootElement.GetProperty("pending_count").GetInt32());
        var pendingFile = document.RootElement.GetProperty("pending_file");
        Assert.True(pendingFile.ValueKind == JsonValueKind.Null || File.Exists(pendingFile.GetString()), result.Envelope.ToJson());
        Assert.DoesNotContain(RunArtifacts.PendingFile(paths.RunDirectory(runId)), result.Summary);
    }

    [Fact]
    public async Task CloseDropsPendingItemsWithoutRunningTheModel()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "close-test", workdir, SessionStates.AwaitingApproval);
        var paths = store.PathsFor(workdir, "close-test");
        var runId = store.CreateRunDirectory(paths, 1, 10);
        var record = store.Load(paths);
        record.Runs.Add(new RunRecord { RunId = runId, Kind = "start", StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, State = SessionStates.AwaitingApproval });
        record.Pending.Add(new PendingRecord { Id = "0123456789", CallId = "call_1", Tool = "shell", Summary = "rm build.log", DeferralMessage = "PENDING APPROVAL 0123456789: rm build.log" });
        store.Save(paths, record);

        var exitCode = await Program.Main(["close", "--name", "close-test"]);
        var after = store.Load(paths);

        Assert.Equal(0, exitCode);
        Assert.Equal(SessionStates.Finished, after.State);
        Assert.Equal(0, after.PendingCount);
        Assert.Null(after.FinalText);

        var result = ReadJson(Path.Combine(paths.RunDirectory(runId), "result.json"));
        Assert.False(result.GetProperty("final_answer").GetBoolean());

        var status = ReadJson(Path.Combine(paths.RunDirectory(runId), "status.json"));
        Assert.Equal(SessionStates.Finished, status.GetProperty("state").GetString());
        Assert.Equal("closed by the supervisor", status.GetProperty("last_activity").GetString());
    }

    [Fact]
    public void ResultWritesTheFinalAnswerToAChosenFile()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "result-test", workdir, SessionStates.Finished);
        var paths = store.PathsFor(workdir, "result-test");
        var runId = store.CreateRunDirectory(paths, 1, 10);
        var record = store.Load(paths);
        record.Runs.Add(new RunRecord { RunId = runId, Kind = "start", StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, State = SessionStates.Finished });
        record.FinalText = "The final answer.";
        store.Save(paths, record);
        File.WriteAllText(RunArtifacts.TextFile(paths.RunDirectory(runId)), "The final answer.");

        var output = Path.Combine(harness.Home, "answer.md");
        var commandLine = CommandLine.Parse(["result", "--name", "result-test", "--output", output], new HashSet<string> { "name", "workdir", "output" }, new HashSet<string>());
        var result = SessionCommands.Result(commandLine);

        Assert.True(File.Exists(output), result.Envelope.ToJson());
        Assert.Equal("The final answer.", File.ReadAllText(output));
        Assert.Contains("Wrote", result.Summary);
        Assert.DoesNotContain("The final answer.", result.Summary);
    }

    [Fact]
    public async Task WaitAcceptsMultipleSessionNames()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();

        foreach (var name in new[] { "alpha", "beta", "gamma" })
        {
            harness.CreateSession(store, name, workdir, SessionStates.Finished);
        }

        var commandLine = CommandLine.Parse(["wait", "--name", "alpha,beta,gamma"], new HashSet<string> { "name", "workdir", "timeout-seconds" }, new HashSet<string>());
        var result = await SessionCommands.WaitAsync(commandLine);

        Assert.Contains("alpha", result.Summary);
        Assert.Contains("beta", result.Summary);
        Assert.Contains("gamma", result.Summary);
        using var document = JsonDocument.Parse(result.Envelope.ToJson());
        Assert.Equal(3, document.RootElement.GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public async Task WaitReportsMixedStatesPerSession()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "done", workdir, SessionStates.Finished);
        harness.CreateSession(store, "broken", workdir, SessionStates.Failed);
        harness.CreateSession(store, "busy", workdir, SessionStates.Running);

        var donePaths = store.PathsFor(workdir, "done");
        var runId = store.CreateRunDirectory(donePaths, 1, 10);
        var doneRecord = store.Load(donePaths);
        var run = new RunRecord { RunId = runId, Kind = "start", StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, State = SessionStates.Finished };
        doneRecord.Runs.Add(run);
        doneRecord.FinalText = "Done.";
        store.Save(donePaths, doneRecord);
        RunArtifacts.WriteResult(donePaths.RunDirectory(runId), doneRecord, run, doneRecord.FinalText);

        using var busyLock = SessionLock.TryAcquire(store.PathsFor(workdir, "busy"));
        Assert.NotNull(busyLock);

        var commandLine = CommandLine.Parse(["wait", "--name", "done,broken", "--name", "busy", "--timeout-seconds", "1"], new HashSet<string> { "name", "workdir", "timeout-seconds" }, new HashSet<string>());
        var result = await SessionCommands.WaitAsync(commandLine);

        Assert.Equal("mixed", result.Envelope.State);
        Assert.Equal(ErrorCode.RunFailed.ToExitCode(), result.ExitCode);

        using var document = JsonDocument.Parse(result.Envelope.ToJson());
        var sessions = document.RootElement.GetProperty("sessions").EnumerateArray().ToDictionary(entry => entry.GetProperty("session").GetString()!, entry => entry.Clone());
        Assert.Equal(3, sessions.Count);
        Assert.Equal(SessionStates.Finished, sessions["done"].GetProperty("state").GetString());
        Assert.True(sessions["done"].GetProperty("final_answer").GetBoolean());
        Assert.True(File.Exists(sessions["done"].GetProperty("result_file").GetString()));
        Assert.Equal(SessionStates.Failed, sessions["broken"].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, sessions["broken"].GetProperty("result_file").ValueKind);
        Assert.Equal(SessionStates.Running, sessions["busy"].GetProperty("state").GetString());
    }

    [Fact]
    public async Task CloseKeepsAnExistingFinalAnswer()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "close-answer", workdir, SessionStates.AwaitingApproval);
        var paths = store.PathsFor(workdir, "close-answer");
        var runId = store.CreateRunDirectory(paths, 1, 10);
        var record = store.Load(paths);
        record.Runs.Add(new RunRecord { RunId = runId, Kind = "start", StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, State = SessionStates.AwaitingApproval });
        record.Pending.Add(new PendingRecord { Id = "0123456789", CallId = "call_1", Tool = "shell", Summary = "rm build.log", DeferralMessage = "PENDING APPROVAL 0123456789: rm build.log" });
        record.FinalText = "The review is complete.";
        store.Save(paths, record);

        var exitCode = await Program.Main(["close", "--name", "close-answer"]);

        Assert.Equal(0, exitCode);
        var result = ReadJson(Path.Combine(paths.RunDirectory(runId), "result.json"));
        Assert.True(result.GetProperty("final_answer").GetBoolean());
        Assert.Equal("The review is complete.", File.ReadAllText(Path.Combine(paths.RunDirectory(runId), "result.md")));
    }

    [Fact]
    public void DoctorReportsReasoningEffortForEachAlias()
    {
        using var harness = new CliHarness();
        harness.WriteConfig("""
            {
              "providers": {
                "test": { "endpoint": "https://example.invalid/v1", "api_key": "test" }
              },
              "models": {
                "fast": { "provider": "test", "model": "fast-model", "reasoning_effort": "high" },
                "slow": { "provider": "test", "model": "slow-model" }
              },
              "default_model": "fast"
            }
            """);

        var commandLine = CommandLine.Parse(["doctor"], new HashSet<string> { "model" }, new HashSet<string>());
        var result = UtilityCommands.Doctor(commandLine);

        var lines = result.Summary.Split('\n');
        var fastLine = Assert.Single(lines, line => line.Contains("fast", StringComparison.Ordinal));
        Assert.Contains("high", fastLine);
        var slowLine = Assert.Single(lines, line => line.Contains("slow", StringComparison.Ordinal));
        Assert.DoesNotContain("high", slowLine);
    }

    [Fact]
    public void ResultTruncatesLongAnswersInTheSummary()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "long-result", workdir, SessionStates.Finished);
        var paths = store.PathsFor(workdir, "long-result");
        var runId = store.CreateRunDirectory(paths, 1, 10);
        var record = store.Load(paths);
        record.Runs.Add(new RunRecord { RunId = runId, Kind = "start", StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, State = SessionStates.Finished });
        record.FinalText = new string('x', 40_000);
        store.Save(paths, record);
        File.WriteAllText(RunArtifacts.TextFile(paths.RunDirectory(runId)), record.FinalText);

        var commandLine = CommandLine.Parse(["result", "--name", "long-result"], new HashSet<string> { "name", "workdir", "output" }, new HashSet<string>());
        var result = SessionCommands.Result(commandLine);

        Assert.Contains("[Truncated at 20000 of 40000 characters.", result.Summary);
        Assert.True(result.Summary.Length < 21_000, result.Summary.Length.ToString());
    }

    [Fact]
    public async Task WaitTimesOutOnARunningSession()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "running-test", workdir, SessionStates.Running);
        var paths = store.PathsFor(workdir, "running-test");

        using var sessionLock = SessionLock.TryAcquire(paths);
        Assert.NotNull(sessionLock);

        var commandLine = CommandLine.Parse(["wait", "--name", "running-test", "--timeout-seconds", "1"], new HashSet<string> { "name", "workdir", "timeout-seconds" }, new HashSet<string>());
        var result = await SessionCommands.WaitAsync(commandLine);

        Assert.Equal(SessionStates.Running, result.Envelope.State);
        Assert.Contains("still running", result.Summary);
    }

    [Fact]
    public void UsageAggregatesAndFiltersRuns()
    {
        using var harness = new CliHarness();
        var usageFile = Path.Combine(harness.Home, "usage.jsonl");
        var recent = DateTimeOffset.UtcNow.AddDays(-1);
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        File.WriteAllLines(usageFile,
        [
            UsageLine("r1", "alpha", "fast", "fast-model", recent, 10, 2, 100, 20, 50),
            UsageLine("r2", "beta", "slow", "slow-model", recent, 20, 4, 200, 0, 80),
            UsageLine("r3", "gamma", "fast", "fast-model", old, 30, 6, 300, 30, 120)
        ]);

        var commandLine = CommandLine.Parse(["usage", "--since", "7d", "--model", "fast"], new HashSet<string> { "since", "session", "model", "workdir" }, new HashSet<string>());
        var result = SessionCommands.Usage(commandLine);

        using var document = JsonDocument.Parse(result.Envelope.ToJson());
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal(10, root.GetProperty("totals").GetProperty("model_calls").GetInt32());
        Assert.Equal(100, root.GetProperty("totals").GetProperty("input_tokens").GetInt64());
        Assert.Equal(1, root.GetProperty("models").GetArrayLength());
        Assert.Equal("fast", root.GetProperty("models")[0].GetProperty("model").GetString());
    }

    [Fact]
    public void UsageKeepsRunsWithTheSameIdAcrossSessions()
    {
        using var harness = new CliHarness();
        var usageFile = Path.Combine(harness.Home, "usage.jsonl");
        var timestamp = DateTimeOffset.UtcNow.AddHours(-1);
        File.WriteAllLines(usageFile,
        [
            UsageLine("0001-20261115-141529", "alpha", "fast", "fast-model", timestamp, 1, 1, 10, 0, 5),
            UsageLine("0001-20261115-141529", "beta", "fast", "fast-model", timestamp, 1, 1, 10, 0, 5)
        ]);

        var commandLine = CommandLine.Parse(["usage"], new HashSet<string> { "since", "session", "model", "workdir" }, new HashSet<string>());
        var result = SessionCommands.Usage(commandLine);

        using var document = JsonDocument.Parse(result.Envelope.ToJson());
        Assert.Equal(2, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("totals").GetProperty("runs").GetInt32());
    }

    [Fact]
    public void UsageSkipsMalformedLines()
    {
        using var harness = new CliHarness();
        var usageFile = Path.Combine(harness.Home, "usage.jsonl");
        File.WriteAllLines(usageFile,
        [
            UsageLine("r1", "alpha", "fast", "fast-model", DateTimeOffset.UtcNow.AddHours(-1), 1, 1, 10, 0, 5),
            "{not json"
        ]);

        var commandLine = CommandLine.Parse(["usage"], new HashSet<string> { "since", "session", "model", "workdir" }, new HashSet<string>());
        var result = SessionCommands.Usage(commandLine);

        using var document = JsonDocument.Parse(result.Envelope.ToJson());
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("skipped_lines").GetInt32());
    }

    [Fact]
    public void CloseWorksWithoutARun()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "close-no-run", workdir, SessionStates.Finished);
        var paths = store.PathsFor(workdir, "close-no-run");

        var commandLine = CommandLine.Parse(["close", "--name", "close-no-run"], new HashSet<string> { "name", "workdir" }, new HashSet<string>());
        var result = SessionCommands.Close(commandLine);

        Assert.Equal(SessionStates.Finished, result.Envelope.State);
        Assert.Equal(SessionStates.Finished, store.Load(paths).State);
    }

    [Fact]
    public void DeleteRemovesTheSession()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "delete-test", workdir, SessionStates.Finished);
        var paths = store.PathsFor(workdir, "delete-test");

        var commandLine = CommandLine.Parse(["delete", "--name", "delete-test"], new HashSet<string> { "name", "workdir" }, new HashSet<string>());
        var result = SessionCommands.Delete(commandLine);

        Assert.Equal("deleted", result.Envelope.State);
        Assert.False(Directory.Exists(paths.Directory));
    }

    private static JsonElement ReadJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string UsageLine(string runId, string session, string alias, string modelId, DateTimeOffset timestamp, int modelCalls, int toolCalls, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        return JsonSerializer.Serialize(new
        {
            schema = 1,
            timestamp = RunArtifacts.FormatTime(timestamp),
            run_id = runId,
            session = session,
            workdir = "D:/work",
            kind = "start",
            state = "finished",
            model_alias = alias,
            model_provider = "test",
            model_id = modelId,
            duration_seconds = 1.5,
            model_calls = modelCalls,
            tool_calls = toolCalls,
            input_tokens = inputTokens,
            cached_input_tokens = cachedInputTokens,
            output_tokens = outputTokens
        });
    }
}

[CollectionDefinition("cli", DisableParallelization = true)]
public sealed class CliCollection
{
}

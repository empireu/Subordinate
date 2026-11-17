using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class StopRequestTests
{
    [Fact]
    public void UserStopRequestIsReadBack()
    {
        using var harness = new SessionHarness();

        StopRequest.Write(harness.Paths, StopRequesters.User, "tui");

        Assert.Equal(StopRequesters.User, StopRequest.ReadRequester(harness.Paths));
    }

    [Fact]
    public void TimestampOnlyStopFileCountsAsSupervisorStop()
    {
        using var harness = new SessionHarness();

        File.WriteAllText(harness.Paths.StopFile, "2026-11-17T12:00:00.0000000+00:00");

        Assert.Equal(StopRequesters.Supervisor, StopRequest.ReadRequester(harness.Paths));
    }

    [Fact]
    public void MissingStopFileCountsAsSupervisorStop()
    {
        using var harness = new SessionHarness();

        Assert.Equal(StopRequesters.Supervisor, StopRequest.ReadRequester(harness.Paths));
    }

    [Fact]
    public async Task UserStopIsRecordedAndTellsTheSupervisorNotToResume()
    {
        using var harness = new SessionHarness();
        var (outcome, record) = await RunAndStopAsync(harness, StopRequesters.User);

        Assert.Equal(SessionStates.Stopped, outcome.State);
        Assert.Equal(StopRequesters.User, record.StoppedBy);
        Assert.Contains(StopRequest.UserStopAgentNote, record.QueuedMessage, StringComparison.Ordinal);
        Assert.Contains(StopRequest.UserStopNotice, File.ReadAllText(Path.Combine(outcome.RunDirectory, "result.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupervisorStopIsNotReportedAsUserStop()
    {
        using var harness = new SessionHarness();
        var (outcome, record) = await RunAndStopAsync(harness, StopRequesters.Supervisor);

        Assert.Equal(SessionStates.Stopped, outcome.State);
        Assert.Equal(StopRequesters.Supervisor, record.StoppedBy);
        Assert.Null(record.QueuedMessage);
        Assert.DoesNotContain(StopRequest.UserStopNotice, File.ReadAllText(Path.Combine(outcome.RunDirectory, "result.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserStopNoteIsDeliveredOnceOnTheNextRun()
    {
        using var harness = new SessionHarness();
        await RunAndStopAsync(harness, StopRequesters.User);
        StopRequest.Clear(harness.Paths);

        var (outcome, record) = await harness.RunAsync(new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "Done.")));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Null(record.StoppedBy);
        Assert.Null(record.QueuedMessage);
        var deliveries = harness.Transcript()
            .Where(message => message.Role == ChatRole.User)
            .Count(message => message.GetText().Contains(StopRequest.UserStopAgentNote, StringComparison.Ordinal));
        Assert.Equal(1, deliveries);
    }

    private static async Task<(RunOutcome Outcome, SessionRecord Record)> RunAndStopAsync(SessionHarness harness, string requestedBy)
    {
        var provider = new BlockingProvider(
            SessionHarness.ShellCalls(("call_1", "git status")),
            SessionHarness.Text(ChatRole.Assistant, "Done."));

        var run = Task.Run(async () => await harness.RunAsync(provider));

        try
        {
            await provider.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            StopRequest.Write(harness.Paths, requestedBy, "test");
            return await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            provider.Release.TrySetResult();
        }
    }
}

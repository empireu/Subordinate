using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core;
using Subordinate.Core.Gate;
using Subordinate.Core.Policy;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class SessionResumeTests
{
    [Fact]
    public async Task DeferredCallIsApprovedOnResumeAndRunsExactlyOnce()
    {
        using var harness = new SessionHarness();
        var expectedId = PendingLedger.DeriveId("call_2");

        var first = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_1", "git status"), ("call_2", "rm build.log")),
            SessionHarness.Text(ChatRole.Assistant, "Waiting for approval."));
        var (firstOutcome, afterFirst) = await harness.RunAsync(first);

        Assert.Equal(SessionStates.AwaitingApproval, firstOutcome.State);
        Assert.Equal(["git status"], harness.Executor.Commands);
        var pending = Assert.Single(afterFirst.Pending);
        Assert.Equal(expectedId, pending.Id);
        Assert.Equal("call_2", pending.CallId);
        Assert.Contains(expectedId, File.ReadAllText(Path.Combine(firstOutcome.RunDirectory, "pending.json")));
        Assert.Contains(expectedId, File.ReadAllText(Path.Combine(firstOutcome.RunDirectory, "pending.md")));
        Assert.Contains(SessionHarness.ToolResults(harness.Transcript()), result => result.CallId == "call_2" && result.Output.StartsWith("PENDING APPROVAL " + expectedId, StringComparison.Ordinal));

        harness.Update(record => new PendingLedger(record).Approve(record.Pending.Single(item => item.Id == expectedId), ApprovalScope.Once, null));

        var second = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "Done."));
        var (secondOutcome, afterSecond) = await harness.RunAsync(second);

        Assert.Equal(SessionStates.Finished, secondOutcome.State);
        Assert.Equal(["git status", "rm build.log"], harness.Executor.Commands);
        var approved = Assert.Single(afterSecond.Pending);
        Assert.Equal(expectedId, approved.Id);
        Assert.Equal(PendingStatuses.Approved, approved.Status);
        Assert.Equal(FrameStates.Succeeded, approved.Execution?.State);
        Assert.True(approved.Delivered);

        var update = Assert.Single(second.Requests)[^1];
        Assert.True(update.Role == ChatRole.User, SessionHarness.Describe(second.Requests[0]));
        Assert.Contains("[Supervisor update]", update.GetText());
        Assert.Contains("APPROVED " + expectedId, update.GetText());
        Assert.Contains("ran: rm build.log", update.GetText());
    }

    [Fact]
    public async Task PendingIdsStayStableAcrossResumesAndFollowTheCallNotThePosition()
    {
        using var harness = new SessionHarness();
        var firstId = PendingLedger.DeriveId("call_2");
        var secondId = PendingLedger.DeriveId("call_3");

        await harness.RunAsync(new ScriptedProvider(
            SessionHarness.ShellCalls(("call_2", "rm build.log")),
            SessionHarness.Text(ChatRole.Assistant, "Waiting.")));

        harness.Update(record => record.QueuedMessage = "Also remove other.log.");

        var (secondOutcome, afterSecond) = await harness.RunAsync(new ScriptedProvider(
            SessionHarness.ShellCalls(("call_3", "rm other.log")),
            SessionHarness.Text(ChatRole.Assistant, "Waiting again.")));

        Assert.Equal(SessionStates.AwaitingApproval, secondOutcome.State);
        Assert.Equal([firstId, secondId], afterSecond.Pending.Select(item => item.Id));
        Assert.All(afterSecond.Pending, item => Assert.Equal(PendingStatuses.Pending, item.Status));
        var pendingJson = File.ReadAllText(Path.Combine(secondOutcome.RunDirectory, "pending.json"));
        Assert.Contains(firstId, pendingJson);
        Assert.Contains(secondId, pendingJson);
        Assert.Empty(harness.Executor.Commands);

        harness.Update(record => new PendingLedger(record).Approve(record.Pending.Single(item => item.Id == secondId), ApprovalScope.Once, null));

        var (thirdOutcome, afterThird) = await harness.RunAsync(new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "One left.")));

        Assert.Equal(SessionStates.AwaitingApproval, thirdOutcome.State);
        Assert.Equal(["rm other.log"], harness.Executor.Commands);
        Assert.Equal([firstId, secondId], afterThird.Pending.Select(item => item.Id));
        Assert.Equal(PendingStatuses.Pending, afterThird.Pending[0].Status);
        Assert.Equal(PendingStatuses.Approved, afterThird.Pending[1].Status);
    }

    [Fact]
    public async Task DeniedCallStaysDeniedWhenTheModelRetriesIt()
    {
        using var harness = new SessionHarness();
        var id = PendingLedger.DeriveId("call_2");

        await harness.RunAsync(new ScriptedProvider(
            SessionHarness.ShellCalls(("call_2", "rm build.log")),
            SessionHarness.Text(ChatRole.Assistant, "Waiting.")));

        harness.Update(record => new PendingLedger(record).Deny(record.Pending.Single(), "keep the log"));

        var second = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_9", "rm build.log")),
            SessionHarness.Text(ChatRole.Assistant, "Gave up."));
        var (outcome, record) = await harness.RunAsync(second);

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Empty(harness.Executor.Commands);
        Assert.Contains("DENIED " + id, second.Requests[0][^1].GetText());
        Assert.Contains("keep the log", second.Requests[0][^1].GetText());
        var retry = Assert.Single(SessionHarness.ToolResults(harness.Transcript()), result => result.CallId == "call_9");
        Assert.Contains("DENIED " + id, retry.Output);
        var item = Assert.Single(record.Pending);
        Assert.Equal(PendingStatuses.Denied, item.Status);
    }

    [Fact]
    public async Task MidTurnResumeRunsOnlyTheUnexecutedCallWithoutPromptingTheModel()
    {
        using var harness = new SessionHarness();
        harness.Update(record =>
        {
            record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.ShellCalls(("call_a", "git status"), ("call_b", "git log -1"))]);
            record.Inflight = new InflightRecord
            {
                AssistantIndex = record.Transcript.Count - 1,
                Frames =
                [
                    new InflightFrameRecord { CallId = "call_a", State = FrameStates.Succeeded, Text = "exit_code: 0\nstdout:\nA-OUTPUT" },
                    new InflightFrameRecord { CallId = "call_b", State = FrameStates.NotStarted }
                ]
            };
            record.State = SessionStates.Running;
        });

        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "Done."));
        var (outcome, record) = await harness.RunAsync(provider);

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(["git log -1"], harness.Executor.Commands);
        Assert.Null(record.Inflight);

        var request = Assert.Single(provider.Requests);
        var results = SessionHarness.ToolResults(request).ToList();
        Assert.Equal(["call_a", "call_b"], results.Select(result => result.CallId));
        Assert.Contains("A-OUTPUT", results[0].Output);
        Assert.Contains("ran: git log -1", results[1].Output);
        Assert.Equal(1, request.Count(message => message.Role == ChatRole.Assistant));
    }

    [Fact]
    public async Task MidTurnResumeReportsAStartedCallAsInterruptedInsteadOfRunningItAgain()
    {
        using var harness = new SessionHarness();
        harness.Update(record =>
        {
            record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.ShellCalls(("call_a", "git status"))]);
            record.Inflight = new InflightRecord
            {
                AssistantIndex = record.Transcript.Count - 1,
                Frames = [new InflightFrameRecord { CallId = "call_a", State = FrameStates.Started }]
            };
            record.State = SessionStates.Running;
        });

        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "Done."));
        var (outcome, _) = await harness.RunAsync(provider);

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Empty(harness.Executor.Commands);
        var result = Assert.Single(SessionHarness.ToolResults(Assert.Single(provider.Requests)));
        Assert.Contains("interrupted", result.Output);
    }

    [Fact]
    public async Task MidTurnResumeRunsAnApprovedDeferredCallInsideTheBatch()
    {
        using var harness = new SessionHarness();
        var id = PendingLedger.DeriveId("call_b");

        var deferralMessage = $"PENDING APPROVAL {id}: rm build.log";

        harness.Update(record =>
        {
            record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.ShellCalls(("call_a", "git status"), ("call_b", "rm build.log"))]);
            record.Inflight = new InflightRecord
            {
                AssistantIndex = record.Transcript.Count - 1,
                Frames =
                [
                    new InflightFrameRecord { CallId = "call_a", State = FrameStates.NotStarted },
                    new InflightFrameRecord { CallId = "call_b", State = FrameStates.Deferred, Text = deferralMessage }
                ]
            };
            record.Pending.Add(new PendingRecord
            {
                Id = id,
                CallId = "call_b",
                Tool = "shell",
                Summary = "rm build.log",
                Command = "rm build.log",
                ArgumentsJson = "{\"command\":\"rm build.log\"}",
                Fingerprint = "test-fingerprint",
                DeferralMessage = deferralMessage
            });
            record.State = SessionStates.Running;
            new PendingLedger(record).Approve(record.Pending.Single(), ApprovalScope.Once, null);
        });

        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "Done."));
        var (outcome, record) = await harness.RunAsync(provider);

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(["git status", "rm build.log"], harness.Executor.Commands);
        var results = SessionHarness.ToolResults(Assert.Single(provider.Requests)).ToList();
        Assert.Equal(["call_a", "call_b"], results.Select(result => result.CallId));
        Assert.Contains("ran: rm build.log", results[1].Output);
        Assert.DoesNotContain(Assert.Single(provider.Requests), message => message.Role == ChatRole.User && message.GetText().Contains("[Supervisor update]"));
        Assert.Equal(FrameStates.Succeeded, record.Pending.Single().Execution?.State);
    }

    [Fact]
    public void ResumeIsRefusedWhenThePendingListDisagreesWithTheTranscript()
    {
        using var harness = new SessionHarness();
        harness.Update(record => record.Pending.Add(new PendingRecord { Id = "0123456789", CallId = "call_missing", Tool = "shell", DeferralMessage = "PENDING APPROVAL 0123456789: x" }));

        var record = harness.Load();
        var exception = Assert.Throws<SubordinateException>(() => SessionRunner.Validate(harness.Store, harness.Paths, record));

        Assert.Equal(ErrorCode.StateConflict, exception.Code);
    }

    [Fact]
    public void ResumeIsRefusedWhenTheInflightBatchDoesNotMatchTheTranscript()
    {
        using var harness = new SessionHarness();
        harness.Update(record =>
        {
            record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.ShellCalls(("call_a", "git status"))]);
            record.Inflight = new InflightRecord
            {
                AssistantIndex = record.Transcript.Count - 1,
                Frames = [new InflightFrameRecord { CallId = "call_other", State = FrameStates.NotStarted }]
            };
        });

        var exception = Assert.Throws<SubordinateException>(() => SessionRunner.Validate(harness.Store, harness.Paths, harness.Load()));

        Assert.Equal(ErrorCode.StateConflict, exception.Code);
    }
}

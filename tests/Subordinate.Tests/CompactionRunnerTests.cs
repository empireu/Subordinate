using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Ai.Providers.Protocols.Mistral;
using Subordinate.Core;
using Subordinate.Core.Configuration;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class CompactionRunnerTests
{
    [Fact]
    public async Task BelowTheThresholdNothingChanges()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 100);
        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(5, 100_000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Null(record.Compaction);
        Assert.Equal(0, record.Compactions);
        Assert.Single(provider.Requests);
        Assert.DoesNotContain(harness.Transcript(), message => message.AuthorName == CompactionAuthors.Notice);
    }

    [Fact]
    public async Task AtTheThresholdTheNoticeStartsTheWindDown()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new BlockingProvider(SessionHarness.ShellCalls(("call_1", "ls")), SessionHarness.Text(ChatRole.Assistant, "unused"));
        using var cancellation = new CancellationTokenSource();

        var run = harness.RunAsync(provider, cancellationToken: cancellation.Token, compaction: Settings(5, 4000));
        await provider.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        var (outcome, record) = await run;

        Assert.Equal(SessionStates.Stopped, outcome.State);
        Assert.Equal(CompactionPhases.WindDown, record.Compaction?.Phase);
        Assert.Equal(1, record.Compaction!.WindDownTurnsUsed);
        var notice = Assert.Single(harness.Transcript(), message => message.AuthorName == CompactionAuthors.Notice);
        Assert.Contains("at most 5 more model turns", notice.GetText(), StringComparison.Ordinal);
        Assert.DoesNotContain(CompactionPrompts.TurnsPlaceholder, notice.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummaryRequestDisablesToolsAndKeepsTheMessagePrefix()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_1", "ls")),
            SessionHarness.Text(ChatRole.Assistant, "summary text"),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(1, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(1, record.Compactions);
        Assert.Equal(3, provider.Requests.Count);
        var windDownRequest = provider.FullRequests[0];
        var summaryRequest = provider.FullRequests[1];
        var windDownMessages = provider.Requests[0];
        var summaryMessages = provider.Requests[1];
        Assert.Equal("none", summaryRequest.ToolChoice);
        Assert.Equal(windDownRequest.Tools!.Select(tool => tool.Name), summaryRequest.Tools!.Select(tool => tool.Name));
        Assert.True(summaryMessages.Count > windDownMessages.Count);
        Assert.Equal(SessionHarness.Describe(windDownMessages), SessionHarness.Describe(summaryMessages.Take(windDownMessages.Count)));
        Assert.Equal(CompactionAuthors.Request, summaryMessages[^1].AuthorName);
    }

    [Fact]
    public async Task OnlyMessageZeroIsASystemMessage()
    {
        using var harness = new SessionHarness();
        var run = await RunCompactionWithApprovalsAsync(harness);

        foreach (var request in run.Provider.Requests)
        {
            Assert.Equal(1, request.Count(message => message.Role == ChatRole.System));
            Assert.Equal(ChatRole.System, request[0].Role);
        }

        var log = LoadLog(harness);

        Assert.Equal(1, log.AllMessages.Count(message => message.Role == ChatRole.System));
        Assert.Equal(ChatRole.System, log.AllMessages[0].Role);
        Assert.Equal(1, log.ActiveMessages.Count(message => message.Role == ChatRole.System));
    }

    [Fact]
    public async Task AReplyWithoutToolCallsMovesStraightToTheSummary()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "I am at a safe point."),
            SessionHarness.Text(ChatRole.Assistant, "summary text"),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(5, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(1, record.Compactions);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal("none", provider.FullRequests[1].ToolChoice);
        Assert.Contains(provider.Requests[1], message => message.AuthorName == CompactionAuthors.Request);
        Assert.DoesNotContain(provider.Requests[1], message => message.Role == ChatRole.Tool);
    }

    [Fact]
    public async Task AValidSummaryCompactsAndContinues()
    {
        using var harness = new SessionHarness();
        var run = await RunCompactionWithApprovalsAsync(harness);

        Assert.Equal(SessionStates.AwaitingApproval, run.Outcome.State);
        Assert.Equal("done", run.Record.FinalText);
        Assert.Equal(1, run.Record.Compactions);
        Assert.Equal(4, run.Provider.Requests.Count);

        var nextMessages = run.Provider.Requests[3];
        var replacement = nextMessages[1].GetText();

        Assert.Equal(4, nextMessages.Count);
        Assert.Equal(ChatRole.System, nextMessages[0].Role);
        Assert.Equal(CompactionAuthors.Summary, nextMessages[1].AuthorName);
        Assert.Contains("# Original task", replacement, StringComparison.Ordinal);
        Assert.Contains("Do the task.", replacement, StringComparison.Ordinal);
        Assert.Contains("# Your summary", replacement, StringComparison.Ordinal);
        Assert.Contains("the summary", replacement, StringComparison.Ordinal);
        Assert.Contains("Still pending: A3 (cat secret).", replacement, StringComparison.Ordinal);
        Assert.Contains("Denied, do not retry: A1 (rm x). Reason: not allowed.", replacement, StringComparison.Ordinal);
        Assert.Contains("# Files written in this session", replacement, StringComparison.Ordinal);
        Assert.Contains($"- {Path.Combine(harness.Workdir, "notes.txt")}", replacement, StringComparison.Ordinal);
        Assert.Contains(nextMessages[2].GetToolCalls(), call => call.CallId == "call_wind");
        Assert.Equal(ChatRole.Tool, nextMessages[3].Role);
    }

    [Fact]
    public async Task TheKeptRangeExcludesTheNoticeRequestAndTrailingAnswer()
    {
        using var harness = new SessionHarness();
        await RunCompactionWithApprovalsAsync(harness);
        var marker = SingleMarker(harness);
        var log = LoadLog(harness);
        var keptMessages = Enumerable.Range(marker.KeptFromLine, marker.KeptToLine - marker.KeptFromLine)
            .Select(line => Assert.IsType<TranscriptMessageEntry>(log.Entries[line]).Message)
            .ToList();

        Assert.NotEmpty(keptMessages);
        Assert.DoesNotContain(keptMessages, message => message.AuthorName is CompactionAuthors.Notice or CompactionAuthors.Request);
        Assert.DoesNotContain(keptMessages, message => message.Role == ChatRole.Assistant && message.GetToolCalls().Count == 0);
        Assert.Contains(keptMessages, message => message.GetToolCalls().Count > 0);
        Assert.Contains(keptMessages, message => message.Role == ChatRole.Tool);
        Assert.Equal(ChatRole.Tool, keptMessages[^1].Role);
    }

    [Fact]
    public async Task WindDownZeroSkipsTheNoticeAndHasAnEmptyRange()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "summary text"),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(1, record.Compactions);
        Assert.Equal(2, provider.Requests.Count);
        Assert.DoesNotContain(provider.Requests[0], message => message.AuthorName == CompactionAuthors.Notice);
        Assert.Equal("none", provider.FullRequests[0].ToolChoice);
        var marker = SingleMarker(harness);
        Assert.Equal(2, marker.KeptFromLine);
        Assert.Equal(marker.KeptFromLine, marker.KeptToLine);
    }

    [Fact]
    public async Task CompactionCallsDoNotCountTowardTheTurnLimit()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "summary text"),
            SessionHarness.ShellCalls(("call_1", "ls")),
            SessionHarness.ShellCalls(("call_2", "ls")),
            SessionHarness.ShellCalls(("call_3", "ls")),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, record) = await harness.RunAsync(provider, Limits(3), compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(5, provider.Requests.Count);
        Assert.Null(provider.FullRequests[3].ToolChoice);
        Assert.Equal("none", provider.FullRequests[4].ToolChoice);
        Assert.Equal(5, record.Counters.ModelCalls);
        Assert.Equal(1, record.Counters.CompactionModelCalls);
        var status = ReadStatus(harness, record);
        Assert.Equal(0, status.GetProperty("turns_remaining").GetInt32());
    }

    [Fact]
    public async Task TheTurnLimitWinsOverStartingACompaction()
    {
        using var harness = new SessionHarness();
        var provider = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_1", "ls")),
            SessionHarness.Text(ChatRole.Assistant, "done"))
        {
            Usage = new ChatUsage { InputTokens = 5000, OutputTokens = 10 }
        };

        var (outcome, record) = await harness.RunAsync(provider, Limits(1), compaction: Settings(5, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Null(record.Compaction);
        Assert.Equal(0, record.Compactions);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal("none", provider.FullRequests[1].ToolChoice);
        Assert.DoesNotContain(provider.Requests, request => request.Any(message => message.AuthorName == CompactionAuthors.Notice));
        Assert.Contains(provider.Requests[1], message => message.GetText().Contains("The turn limit is reached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheNoticeWinsOverTheLimitWarningAndTheWarningComesLater()
    {
        using var harness = new SessionHarness();
        var responses = new List<ChatMessage>();

        for (var index = 0; index < 6; index++)
        {
            responses.Add(SessionHarness.ShellCalls(($"normal_{index}", "ls")));
        }

        responses.Add(SessionHarness.ShellCalls(("wind", "ls")));
        responses.Add(SessionHarness.Text(ChatRole.Assistant, "summary text"));
        responses.Add(SessionHarness.ShellCalls(("after", "ls")));
        responses.Add(SessionHarness.ShellCalls(("late", "ls")));
        responses.Add(SessionHarness.Text(ChatRole.Assistant, "done"));

        var provider = new ScriptedProvider([.. responses]);
        var usages = new Queue<ChatUsage?>();

        for (var index = 0; index < 5; index++)
        {
            usages.Enqueue(null);
        }

        usages.Enqueue(new ChatUsage { InputTokens = 2000, OutputTokens = 5 });
        provider.Usages = usages;

        var (outcome, record) = await harness.RunAsync(provider, Limits(8), compaction: Settings(1, 1000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(1, record.Compactions);
        Assert.Equal(11, provider.Requests.Count);
        Assert.Contains(provider.Requests[6], message => message.AuthorName == CompactionAuthors.Notice);
        Assert.DoesNotContain("model turns left", SessionHarness.Describe(provider.Requests[6]), StringComparison.Ordinal);
        Assert.Contains("model turns left", SessionHarness.Describe(provider.Requests[8]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolCallsDuringTheSummaryAreRefusedAndTheRequestIsInsertedAgain()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "safe now"),
            SessionHarness.ShellCalls(("call_refused", "ls")),
            SessionHarness.Text(ChatRole.Assistant, "summary text"),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(1, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(1, record.Compactions);
        Assert.Empty(harness.Executor.Commands);
        var log = LoadLog(harness);
        var refusal = log.AllMessages.SelectMany(message => message.Contents).OfType<ToolResultContent>()
            .Single(result => result.CallId == "call_refused");
        Assert.StartsWith("NOT EXECUTED: tools are disabled while you write the compaction summary.", refusal.Output, StringComparison.Ordinal);
        Assert.Equal(2, log.AllMessages.Count(message => message.AuthorName == CompactionAuthors.Request));
        Assert.Equal(4, provider.Requests.Count);
    }

    [Fact]
    public async Task TwoUnusableSummariesFailAndAResumeCompletesTheCompaction()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var failingProvider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, string.Empty),
            SessionHarness.Text(ChatRole.Assistant, "   "));

        var (outcome, record) = await harness.RunAsync(failingProvider, compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Failed, outcome.State);
        Assert.Equal("compaction_failed", outcome.Error?.Code);
        Assert.Equal(CompactionPhases.Summarizing, record.Compaction?.Phase);
        Assert.Equal(0, record.Compactions);

        var resumeProvider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "good summary"),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (resumeOutcome, resumed) = await harness.RunAsync(resumeProvider, compaction: CompactionSettings.Disabled);

        Assert.Equal(SessionStates.Finished, resumeOutcome.State);
        Assert.Equal(1, resumed.Compactions);
        Assert.Equal(2, resumeProvider.Requests.Count);
        Assert.Equal(CompactionAuthors.Request, resumeProvider.Requests[0][^1].AuthorName);
        Assert.Equal(ChatRole.Assistant, resumeProvider.Requests[0][^2].Role);
        Assert.NotEqual(CompactionAuthors.Request, resumeProvider.Requests[0][^2].AuthorName);
    }

    [Fact]
    public async Task ASummaryWithFinishReasonLengthIsNotUsable()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "truncated summary"),
            SessionHarness.Text(ChatRole.Assistant, "good summary"),
            SessionHarness.Text(ChatRole.Assistant, "done"))
        {
            FinishReasons = new Queue<ChatFinishReason>([ChatFinishReason.Length, ChatFinishReason.Stop, ChatFinishReason.Stop])
        };

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(1, record.Compactions);
        Assert.Equal(3, provider.Requests.Count);
        var marker = SingleMarker(harness);
        Assert.Contains("good summary", marker.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated summary", marker.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderErrorDuringTheSummaryIsRetriedOnResume()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var failingProvider = new ScriptedProvider();

        var (outcome, record) = await harness.RunAsync(failingProvider, compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Failed, outcome.State);
        Assert.Equal(CompactionPhases.Summarizing, record.Compaction?.Phase);
        Assert.Equal(0, record.Compactions);

        var resumeProvider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "good summary"),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (resumeOutcome, resumed) = await harness.RunAsync(resumeProvider, compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Finished, resumeOutcome.State);
        Assert.Equal(1, resumed.Compactions);
        Assert.Equal(2, resumeProvider.Requests.Count);
        Assert.Equal(CompactionAuthors.Request, resumeProvider.Requests[0][^1].AuthorName);
        Assert.Equal(1, resumeProvider.Requests[0].Count(message => message.AuthorName == CompactionAuthors.Request));
    }

    [Fact]
    public async Task AfterACompactionContextTokensIsTheNewEstimate()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "summary text"),
            SessionHarness.ShellCalls(("call_1", "ls")),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(1, record.Compactions);
        Assert.Equal(3, provider.Requests.Count);
        Assert.True(record.ContextTokens < 4000, record.ContextTokens.ToString());
        Assert.Equal(1, LoadLog(harness).CompactionCount);
        Assert.DoesNotContain(provider.Requests[2], message => message.AuthorName == CompactionAuthors.Notice);
    }

    [Fact]
    public async Task AStopDuringTheWindDownIsSavedAndTheResumeContinuesIt()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var blockingProvider = new BlockingProvider(SessionHarness.ShellCalls(("call_1", "ls")), SessionHarness.Text(ChatRole.Assistant, "unused"));
        using var cancellation = new CancellationTokenSource();

        var run = harness.RunAsync(blockingProvider, cancellationToken: cancellation.Token, compaction: Settings(2, 4000));
        await blockingProvider.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        var (stoppedOutcome, stoppedRecord) = await run;

        Assert.Equal(SessionStates.Stopped, stoppedOutcome.State);
        Assert.Equal(CompactionPhases.WindDown, stoppedRecord.Compaction?.Phase);
        Assert.Equal(1, stoppedRecord.Compaction!.WindDownTurnsUsed);

        var resumeProvider = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_2", "ls")),
            SessionHarness.Text(ChatRole.Assistant, "summary text"),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (resumeOutcome, resumed) = await harness.RunAsync(resumeProvider, compaction: Settings(2, 4000));

        Assert.Equal(SessionStates.Finished, resumeOutcome.State);
        Assert.Equal(1, resumed.Compactions);
        Assert.Null(resumeProvider.FullRequests[0].ToolChoice);
        Assert.DoesNotContain(resumeProvider.Requests[0], message => message.AuthorName == CompactionAuthors.Request);
        Assert.Contains(resumeProvider.Requests[0], message => message.AuthorName == CompactionAuthors.Notice);
    }

    [Fact]
    public async Task AResumedCompactionSendsTheSameReplacementAndRequest()
    {
        using var reference = new SessionHarness();
        SetContextTokens(reference, 5000);
        var referenceProvider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "summary text"),
            SessionHarness.Text(ChatRole.Assistant, "done"));
        await reference.RunAsync(referenceProvider, compaction: Settings(0, 4000));

        using var resumedHarness = new SessionHarness();
        SetContextTokens(resumedHarness, 5000);
        var failedProvider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "summary text"));
        var (failedOutcome, _) = await resumedHarness.RunAsync(failedProvider, compaction: Settings(0, 4000));
        Assert.Equal(SessionStates.Failed, failedOutcome.State);
        Assert.Equal(2, failedProvider.Requests.Count);

        var resumeProvider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
        var (resumeOutcome, _) = await resumedHarness.RunAsync(resumeProvider, compaction: CompactionSettings.Disabled);

        Assert.Equal(SessionStates.Finished, resumeOutcome.State);
        Assert.Single(resumeProvider.Requests);
        Assert.Equal(SessionHarness.Describe(referenceProvider.Requests[1]), SessionHarness.Describe(resumeProvider.Requests[0]));
        Assert.Equal(SessionHarness.Describe(failedProvider.Requests[1]), SessionHarness.Describe(resumeProvider.Requests[0]));
        Assert.Equal(referenceProvider.Requests[1][1].GetText(), resumeProvider.Requests[0][1].GetText());
        Assert.Equal(CompactionAuthors.Summary, resumeProvider.Requests[0][1].AuthorName);
    }

    [Fact]
    public void AuthorNameDoesNotChangeTheMistralRequest()
    {
        var plain = new ChatMessage { Role = ChatRole.User, Contents = [new TextContent { Text = "hello" }] };
        var tagged = new ChatMessage { Role = ChatRole.User, AuthorName = CompactionAuthors.Notice, Contents = [new TextContent { Text = "hello" }] };
        var firstRequest = new ChatRequest { Model = "test-model", Messages = [SessionHarness.Text(ChatRole.System, "base"), plain] };
        var secondRequest = new ChatRequest { Model = "test-model", Messages = [SessionHarness.Text(ChatRole.System, "base"), tagged] };

        var firstJson = JsonSerializer.Serialize(MistralRequestMapper.Map(firstRequest));
        var secondJson = JsonSerializer.Serialize(MistralRequestMapper.Map(secondRequest));

        Assert.Equal(firstJson, secondJson);
    }

    [Fact]
    public async Task TwoCompactionsKeepTheOriginalTask()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "first summary"),
            SessionHarness.ShellCalls(("call_1", "ls")),
            SessionHarness.ShellCalls(("call_2", "ls")),
            SessionHarness.Text(ChatRole.Assistant, "second summary"),
            SessionHarness.Text(ChatRole.Assistant, "done"))
        {
            Usages = new Queue<ChatUsage?>([null, new ChatUsage { InputTokens = 1000, OutputTokens = 10 }, new ChatUsage { InputTokens = 5000, OutputTokens = 10 }, null, null])
        };

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(2, record.Compactions);
        var markers = LoadLog(harness).Entries.OfType<TranscriptCompactionEntry>().Select(entry => entry.Marker).ToList();
        Assert.Equal(2, markers.Count);
        Assert.Contains("# Original task", markers[0].Message, StringComparison.Ordinal);
        Assert.Contains("# Original task", markers[1].Message, StringComparison.Ordinal);
        Assert.Contains("Do the task.", markers[1].Message, StringComparison.Ordinal);
        Assert.Contains("second summary", markers[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextStillAboveTheThresholdAfterCompactionFails()
    {
        using var harness = new SessionHarness();
        SetContextTokens(harness, 5000);
        var provider = new ScriptedProvider(
            SessionHarness.Text(ChatRole.Assistant, "summary text"),
            SessionHarness.ShellCalls(("call_1", "ls")),
            SessionHarness.Text(ChatRole.Assistant, "unused"))
        {
            Usages = new Queue<ChatUsage?>([null, new ChatUsage { InputTokens = 4500, OutputTokens = 10 }, null])
        };

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Failed, outcome.State);
        Assert.Equal("compaction_failed", outcome.Error?.Code);
        Assert.Contains("4500", outcome.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(1, record.Compactions);
        Assert.Null(record.Compaction);
        Assert.Equal(2, provider.Requests.Count);
    }

    private static async Task<(ScriptedProvider Provider, RunOutcome Outcome, SessionRecord Record)> RunCompactionWithApprovalsAsync(SessionHarness harness)
    {
        var initial = harness.Load();
        initial.Transcript = harness.Store.AppendTranscript(harness.Paths, initial.Transcript,
        [
            SessionHarness.ShellCalls(("call_pending", "cat secret")),
            ToolResult("call_pending", "PENDING APPROVAL A3: cat secret"),
            SessionHarness.ShellCalls(("call_denied", "rm x")),
            ToolResult("call_denied", "DENIED A1: no")
        ]);
        initial.Pending.Add(new PendingRecord
        {
            Id = "A3",
            CallId = "call_pending",
            Status = PendingStatuses.Pending,
            DeferralMessage = "PENDING APPROVAL A3: cat secret",
            Summary = "cat secret"
        });
        initial.Pending.Add(new PendingRecord
        {
            Id = "A1",
            CallId = "call_denied",
            Status = PendingStatuses.Denied,
            Summary = "rm x",
            Decision = new DecisionRecord { Reason = "not allowed" }
        });
        initial.FilesWritten = [Path.Combine(harness.Workdir, "notes.txt")];
        initial.ContextTokens = 5000;
        harness.Store.Save(harness.Paths, initial);

        var provider = new ScriptedProvider(
            SessionHarness.ShellCalls(("call_wind", "ls")),
            SessionHarness.Text(ChatRole.Assistant, "safe now"),
            SessionHarness.Text(ChatRole.Assistant, "the summary"),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, record) = await harness.RunAsync(provider, compaction: Settings(2, 4000));
        return (provider, outcome, record);
    }

    private static CompactionSettings Settings(int windDownTurns, int threshold)
    {
        return new CompactionSettings
        {
            Threshold = threshold,
            WindDownTurns = windDownTurns,
            NoticeText = CompactionPrompts.FormatNotice(CompactionPrompts.DefaultNotice, windDownTurns),
            RequestText = CompactionPrompts.DefaultRequest
        };
    }

    private static RunLimits Limits(int maxModelCalls)
    {
        return new RunLimits { MaxModelCalls = maxModelCalls, MaxDuration = TimeSpan.FromMinutes(1) };
    }

    private static void SetContextTokens(SessionHarness harness, long tokens)
    {
        harness.Update(record => record.ContextTokens = tokens);
    }

    private static ChatMessage ToolResult(string callId, string output)
    {
        return new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents = [new ToolResultContent { CallId = callId, ToolName = "shell", Output = output }]
        };
    }

    private static TranscriptLog LoadLog(SessionHarness harness)
    {
        return harness.Store.LoadTranscriptLog(harness.Paths, harness.Load().Transcript);
    }

    private static CompactionMarker SingleMarker(SessionHarness harness)
    {
        return LoadLog(harness).Entries.OfType<TranscriptCompactionEntry>().Single().Marker;
    }

    private static JsonElement ReadStatus(SessionHarness harness, SessionRecord record)
    {
        var path = Path.Combine(harness.Paths.RunDirectory(record.CurrentRun!.RunId), "status.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}

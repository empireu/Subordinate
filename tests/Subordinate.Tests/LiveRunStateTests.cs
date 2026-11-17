using System.Text;
using Subordinate.Core.Monitoring;

namespace Subordinate.Tests;

public class LiveRunStateTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void HelloStoresIdentityAndStartingPhase()
    {
        var state = new LiveRunState();

        state.Apply(Hello());

        Assert.Equal("/home/sessions/a", state.SessionDirectory);
        Assert.Equal("a", state.Session);
        Assert.Equal("/work", state.Workdir);
        Assert.Equal("0001-20261117-101500", state.RunId);
        Assert.Equal("start", state.RunKind);
        Assert.Equal(1234, state.ProcessId);
        Assert.Equal("alias", state.ModelAlias);
        Assert.Equal("model-id", state.ModelId);
        Assert.Equal(Time, state.StartedAt);
        Assert.Equal("starting", state.Phase);
        Assert.Equal(Time, state.PhaseStartedAt);
    }

    [Fact]
    public void SnapshotReplacesEverythingExceptIdentity()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(1, Time));
        state.Apply(new MonitorReasoningDelta("old reasoning"));
        state.Apply(new MonitorTextDelta("old text"));
        state.Apply(new MonitorToolCallProposed("call_1", "shell", "ls"));
        state.Apply(new MonitorToolStarted("call_1", "shell", "ls", Time));
        state.Apply(new MonitorStatus("running", 5, 7, 1, "waiting", Counters(1), Counters(2), 11));
        state.Apply(new MonitorTranscriptCommitted(3, 300));
        state.Apply(new MonitorTurnCompleted("stop", 100, 20, 30, Time.AddSeconds(4)));

        state.Apply(new MonitorSnapshot(
            "waiting_for_model",
            Time.AddSeconds(10),
            9,
            "new reasoning",
            99,
            "new text",
            88,
            [new MonitorProposedCall("call_9", "read_file", "a.txt")],
            null,
            null,
            77,
            new MonitorLastTurn(1.5, 7)));

        Assert.Equal("a", state.Session);
        Assert.Equal("waiting_for_model", state.Phase);
        Assert.Equal(Time.AddSeconds(10), state.PhaseStartedAt);
        Assert.Equal(9, state.ModelCall);
        Assert.Equal("new reasoning", state.Reasoning);
        Assert.Equal(99, state.ReasoningCharacters);
        Assert.Equal("new text", state.Text);
        Assert.Equal(88, state.TextCharacters);
        Assert.Equal("call_9", Assert.Single(state.ProposedCalls).CallId);
        Assert.Null(state.ActiveTool);
        Assert.Null(state.Status);
        Assert.Equal(77, state.TranscriptCount);
        Assert.Equal(1.5, state.LastTurn!.DurationSeconds);
        Assert.Null(state.LastFinishedTool);
        Assert.Null(state.LastToolFirstLine);
    }

    [Fact]
    public void TurnStartedClearsLiveContent()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(1, Time));
        state.Apply(new MonitorReasoningDelta("reasoning"));
        state.Apply(new MonitorTextDelta("text"));
        state.Apply(new MonitorToolCallProposed("call_1", "shell", "ls"));

        state.Apply(new MonitorTurnStarted(2, Time.AddSeconds(5)));

        Assert.Equal(2, state.ModelCall);
        Assert.Equal("waiting_for_model", state.Phase);
        Assert.Equal(Time.AddSeconds(5), state.PhaseStartedAt);
        Assert.Equal(string.Empty, state.Reasoning);
        Assert.Equal(string.Empty, state.Text);
        Assert.Empty(state.ProposedCalls);
    }

    [Fact]
    public void ReasoningDeltaAppendsAndCounts()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(1, Time));

        state.Apply(new MonitorReasoningDelta("ab"));
        state.Apply(new MonitorReasoningDelta("cd"));

        Assert.Equal("abcd", state.Reasoning);
        Assert.Equal(4, state.ReasoningCharacters);
        Assert.Equal("reasoning", state.Phase);
    }

    [Fact]
    public void TextDeltaAppendsAndCounts()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(1, Time));

        state.Apply(new MonitorTextDelta("ab"));
        state.Apply(new MonitorTextDelta("cd"));

        Assert.Equal("abcd", state.Text);
        Assert.Equal(4, state.TextCharacters);
        Assert.Equal("answering", state.Phase);
    }

    [Fact]
    public void ToolCallProposedAddsUpToTheCap()
    {
        var state = new LiveRunState();

        for (var index = 0; index < 100; index++)
        {
            state.Apply(new MonitorToolCallProposed("call_" + index, "shell", "ls"));
        }

        Assert.Equal(MonitorProtocol.MaxProposedCalls, state.ProposedCalls.Count);
        Assert.Equal("call_0", state.ProposedCalls[0].CallId);
        Assert.Equal("call_63", state.ProposedCalls[^1].CallId);
    }

    [Fact]
    public void TurnCompletedStoresLastTurn()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(1, Time));

        state.Apply(new MonitorTurnCompleted("stop", 100, 20, 30, Time.AddSeconds(12.5)));

        Assert.Equal("turn_done", state.Phase);
        Assert.Equal(12.5, state.LastTurn!.DurationSeconds);
        Assert.Equal(30, state.LastTurn.OutputTokens);
    }

    [Fact]
    public void TurnFailedSetsTurnDoneAndMessage()
    {
        var state = new LiveRunState();
        state.Apply(Hello());

        state.Apply(new MonitorTurnFailed("boom"));

        Assert.Equal("turn_done", state.Phase);
        Assert.Equal("boom", state.LastTurnError);
    }

    [Fact]
    public void ToolStartedSetsActiveTool()
    {
        var state = new LiveRunState();
        state.Apply(Hello());

        state.Apply(new MonitorToolStarted("call_1", "shell", "ls", Time));

        Assert.Equal("running_tools", state.Phase);
        Assert.Equal("call_1", state.ActiveTool!.CallId);
        Assert.Equal("shell", state.ActiveTool.Tool);
        Assert.Equal("ls", state.ActiveTool.Summary);
        Assert.Equal(Time, state.ActiveTool.StartedAt);
    }

    [Fact]
    public void ToolFinishedClearsMatchingActiveTool()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorToolStarted("call_1", "shell", "ls", Time));

        state.Apply(new MonitorToolFinished("call_2", true, "other", Time));
        Assert.NotNull(state.ActiveTool);

        state.Apply(new MonitorToolFinished("call_1", false, "failed", Time));

        Assert.Null(state.ActiveTool);
        Assert.Equal("shell", state.LastFinishedTool);
        Assert.Equal("failed", state.LastToolFirstLine);
    }

    [Fact]
    public void StatusStoresPayload()
    {
        var state = new LiveRunState();
        state.Apply(Hello());

        state.Apply(new MonitorStatus("running", 12.5, 7, 1, "waiting", Counters(1), Counters(2), 11));

        Assert.Equal("running", state.Status!.State);
        Assert.Equal(12.5, state.Status.ElapsedSeconds);
        Assert.Equal(7, state.Status.TurnsRemaining);
        Assert.Equal(1, state.Status.PendingCount);
        Assert.Equal("waiting", state.Status.LastActivity);
        Assert.Equal(1, state.Status.Run.ModelCalls);
        Assert.Equal(2, state.Status.SessionTotals.ModelCalls);
        Assert.Equal(11, state.Status.ContextTokens);
    }

    [Fact]
    public void TranscriptCommittedClearsLiveContentOnlyAfterTurnDone()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(1, Time));
        state.Apply(new MonitorReasoningDelta("reasoning"));
        state.Apply(new MonitorTextDelta("text"));
        state.Apply(new MonitorToolCallProposed("call_1", "shell", "ls"));

        state.Apply(new MonitorTranscriptCommitted(3, 300));
        Assert.Equal("reasoning", state.Reasoning);
        Assert.Equal("text", state.Text);
        Assert.Single(state.ProposedCalls);
        Assert.Equal(3, state.TranscriptCount);

        state.Apply(new MonitorTurnCompleted("stop", null, null, null, Time.AddSeconds(1)));
        state.Apply(new MonitorTranscriptCommitted(4, 400));

        Assert.Equal(string.Empty, state.Reasoning);
        Assert.Equal(string.Empty, state.Text);
        Assert.Empty(state.ProposedCalls);
        Assert.Equal(4, state.TranscriptCount);
    }

    [Fact]
    public void RunEndedSetsEndedPhase()
    {
        var state = new LiveRunState();
        state.Apply(Hello());

        state.Apply(new MonitorRunEnded("finished", "oops"));

        Assert.Equal("ended", state.Phase);
        Assert.Equal("finished", state.EndedState);
        Assert.Equal("oops", state.ErrorMessage);
    }

    [Fact]
    public void ReasoningTailKeepsTheLastCharacters()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(1, Time));

        state.Apply(new MonitorReasoningDelta(new string('a', 50_000)));
        state.Apply(new MonitorReasoningDelta(new string('b', 50_000)));

        Assert.Equal(100_000, state.ReasoningCharacters);
        Assert.Equal(MonitorProtocol.LiveTextTailCharacters, state.Reasoning.Length);
        Assert.All(state.Reasoning, character => Assert.Equal('b', character));
    }

    [Fact]
    public void SnapshotTailsNeverExceedTheCap()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(1, Time));

        state.Apply(new MonitorReasoningDelta(new string('a', 50_000)));
        state.Apply(new MonitorTextDelta(new string('t', 40_000)));
        var snapshot = state.CreateSnapshot();

        Assert.Equal(MonitorProtocol.LiveTextTailCharacters, snapshot.Reasoning.Length);
        Assert.Equal(MonitorProtocol.LiveTextTailCharacters, snapshot.Text.Length);
        Assert.Equal(50_000, snapshot.ReasoningCharacters);
        Assert.Equal(40_000, snapshot.TextCharacters);
    }

    [Fact]
    public void ReasoningTailDoesNotSplitSurrogatePairs()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(1, Time));
        var pair = "\ud83d\ude00";

        state.Apply(new MonitorReasoningDelta(string.Concat(Enumerable.Repeat(pair, 10_000))));
        state.Apply(new MonitorReasoningDelta("b"));
        state.Apply(new MonitorReasoningDelta(string.Concat(Enumerable.Repeat(pair, 20_000))));
        state.Apply(new MonitorReasoningDelta(new string('c', 10_001)));

        var tail = state.Reasoning;
        Assert.Equal(70_002, state.ReasoningCharacters);
        Assert.Equal(MonitorProtocol.LiveTextTailCharacters - 1, tail.Length);
        Assert.True(char.IsHighSurrogate(tail[0]));
        Assert.True(char.IsLowSurrogate(tail[1]));
        Assert.Equal('c', tail[^1]);
    }

    [Fact]
    public void SnapshotRoundTrip()
    {
        var state = new LiveRunState();
        state.Apply(Hello());
        state.Apply(new MonitorTurnStarted(3, Time));
        state.Apply(new MonitorReasoningDelta("thinking"));
        state.Apply(new MonitorTextDelta("answer"));
        state.Apply(new MonitorToolCallProposed("call_1", "shell", "ls"));
        state.Apply(new MonitorToolStarted("call_1", "shell", "ls", Time));
        state.Apply(new MonitorStatus("running", 12.5, 7, 1, "waiting", Counters(1), Counters(2), 11));
        state.Apply(new MonitorTranscriptCommitted(7, 700));
        state.Apply(new MonitorTurnCompleted("stop", 100, 20, 30, Time.AddSeconds(12)));

        var line = MonitorProtocol.Serialize(state.CreateSnapshot());
        var parsed = MonitorProtocol.Parse(Encoding.UTF8.GetBytes(line));
        Assert.Equal(MonitorParseOutcome.Ok, parsed.Outcome);
        var fresh = new LiveRunState();
        fresh.Apply(Hello());
        fresh.Apply(Assert.IsType<MonitorSnapshot>(parsed.Message));

        Assert.Equal(state, fresh);
    }

    private static MonitorHello Hello()
    {
        return new MonitorHello("/home/sessions/a", "a", "/work", "0001-20261117-101500", "start", 1234, "alias", "model-id", Time);
    }

    private static MonitorStatusCounters Counters(int modelCalls)
    {
        return new MonitorStatusCounters(modelCalls, 2, 3, 4, 5);
    }
}

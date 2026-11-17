using Subordinate.Core.Monitoring;
using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;
using Subordinate.Tui.Live;
using Subordinate.Tui.Presentation;

namespace Subordinate.Tests;

public class TuiFormattingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1.0k")]
    [InlineData(18_234, "18.2k")]
    [InlineData(182_345, "182k")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(12_000_000, "12M")]
    [InlineData(999_999, "1.0M")]
    [InlineData(99_999, "100k")]
    [InlineData(9_999_999, "10M")]
    public void FormatTokensMatchesTheExamples(long value, string expected)
    {
        Assert.Equal(expected, Units.FormatTokens(value));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59.9, "59s")]
    [InlineData(192, "3m12s")]
    [InlineData(845, "14m05s")]
    [InlineData(3_840, "1h04m")]
    [InlineData(183_600, "2d03h")]
    public void FormatDurationMatchesTheExamples(double seconds, string expected)
    {
        Assert.Equal(expected, Units.FormatDuration(seconds));
    }

    [Fact]
    public void TextWrapWrapsAPlainSentence()
    {
        var lines = TextWrap.Wrap("the quick brown fox jumps over the lazy dog", 10, 0);

        Assert.Equal(["the quick", "brown fox", "jumps over", "the lazy", "dog"], lines);
    }

    [Fact]
    public void TextWrapHardSplitsALongWord()
    {
        var lines = TextWrap.Wrap("abcdefghijklmnopqrstuvwxyz", 10, 0);

        Assert.Equal(["abcdefghij", "klmnopqrst", "uvwxyz"], lines);
    }

    [Fact]
    public void TextWrapCountsWideCharactersAsTwoColumns()
    {
        var lines = TextWrap.Wrap("\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587", 10, 0);

        Assert.Equal(["\u4e2d\u6587\u4e2d\u6587\u4e2d", "\u6587"], lines);
    }

    [Fact]
    public void TextWrapExpandsTabs()
    {
        var lines = TextWrap.Wrap("a\tb", 10, 0);

        Assert.Equal(["a   b"], lines);
    }

    [Fact]
    public void TextWrapKeepsEmptyLines()
    {
        var lines = TextWrap.Wrap("a\n\nb", 10, 0);

        Assert.Equal(["a", "", "b"], lines);
    }

    [Fact]
    public void TextWrapIndentsContinuationLines()
    {
        var lines = TextWrap.Wrap("abcdefghijklmnopqrstuvwxyz", 10, 2);

        Assert.Equal(["abcdefghij", "  klmnopqr", "  stuvwxyz"], lines);
    }

    [Fact]
    public void TextWrapTreatsWidthsBelowTenAsTen()
    {
        var lines = TextWrap.Wrap("abcdefghijkl", 3, 0);

        Assert.Equal(["abcdefghij", "kl"], lines);
    }

    [Fact]
    public void SessionRowFormatterDropsColumnsAsTheWidthShrinks()
    {
        var session = CreateSession(
            "review-parser",
            "running",
            currentRun: CreateRun(12, 31, 182_000, 141_000, 9_800, Now.AddSeconds(-192)),
            sessionCounters: new CountersRecord { ModelCalls = 12, ToolCalls = 31, InputTokens = 182_000, CachedInputTokens = 141_000, OutputTokens = 9_800 },
            contextTokens: 23_400,
            elapsedSeconds: 192);
        const string activity = "waiting for model 3s";

        var wide = SessionRowFormatter.FormatRow(session, false, activity, 0, 120, Now);
        var medium = SessionRowFormatter.FormatRow(session, false, activity, 0, 90, Now);
        var narrow = SessionRowFormatter.FormatRow(session, false, activity, 0, 60, Now);

        Assert.Equal(Join(
            Pad(".", 1),
            Pad("running", 11),
            Pad("review-parser", 20),
            Pad("deepseek-high", 14),
            Pad("3m12s", 7),
            Pad("12/100", 8),
            Pad("182k/9.8k", 12),
            Pad("23.4k", 7),
            activity), wide);
        Assert.Equal(Join(
            Pad(".", 1),
            Pad("running", 11),
            Pad("review-parser", 20),
            Pad("3m12s", 7),
            Pad("12/100", 8),
            Pad("23.4k", 7),
            activity), medium);
        Assert.Equal(Join(
            Pad(".", 1),
            Pad("running", 11),
            Pad("review-parser", 20),
            Pad("3m12s", 7),
            "waiting for mo..."), narrow);
    }

    [Fact]
    public void SessionRowFormatterFormatsApprovalAndFinishedSessions()
    {
        var approval = CreateSession(
            "approval-one",
            "approval",
            modelAlias: "glm",
            maxTurns: 50,
            currentRun: CreateRun(5, 3, 1_000, 500, 340, Now.AddSeconds(-300), Now.AddSeconds(-180)),
            sessionCounters: new CountersRecord { ModelCalls = 5, ToolCalls = 3, InputTokens = 1_000, CachedInputTokens = 500, OutputTokens = 340 },
            pendingCount: 2,
            needsHumanCount: 1,
            contextTokens: 1_000,
            updatedAt: Now.AddSeconds(-180));
        var finished = CreateSession(
            "finished-one",
            "finished",
            modelAlias: "glm",
            maxTurns: 20,
            currentRun: CreateRun(20, 15, 10_000, 5_000, 2_000, Now.AddSeconds(-1_000), Now.AddSeconds(-720)),
            sessionCounters: new CountersRecord { ModelCalls = 20, ToolCalls = 15, InputTokens = 10_000, CachedInputTokens = 5_000, OutputTokens = 2_000 },
            contextTokens: 10_000,
            updatedAt: Now.AddSeconds(-720));

        var approvalRow = SessionRowFormatter.FormatRow(approval, false, "2 pending (1 needs a human)", 0, 120, Now);
        var finishedRow = SessionRowFormatter.FormatRow(finished, false, "finished 12m ago", 0, 120, Now);

        Assert.Equal(Join(
            Pad("!", 1),
            Pad("approval", 11),
            Pad("approval-one", 20),
            Pad("glm", 14),
            Pad("2m00s", 7),
            Pad("5/50", 8),
            Pad("1.0k/340", 12),
            Pad("1.0k", 7),
            "2 pending (1 needs a human)"), approvalRow);
        Assert.Equal(Join(
            Pad(" ", 1),
            Pad("finished", 11),
            Pad("finished-one", 20),
            Pad("glm", 14),
            Pad("4m40s", 7),
            Pad("20/20", 8),
            Pad("10.0k/2.0k", 12),
            Pad("10.0k", 7),
            "finished 12m ago"), finishedRow);
    }

    [Fact]
    public void SessionRowFormatterCutsLongNamesAndShowsTheSpinner()
    {
        var session = CreateSession("abcdefghijklmnopqrstuvwxyz1234", "running");

        var row = SessionRowFormatter.FormatRow(session, true, "thinking", 1, 120, Now);

        Assert.Contains("abcdefghijklmnopq...", row);
        Assert.StartsWith("/", row);
    }

    [Fact]
    public void SessionRowFormatterHeaderMatchesTheColumns()
    {
        var wide = SessionRowFormatter.FormatHeader(120);
        var medium = SessionRowFormatter.FormatHeader(90);
        var narrow = SessionRowFormatter.FormatHeader(60);

        Assert.Contains("state", wide);
        Assert.Contains("model", wide);
        Assert.Contains("in/out", wide);
        Assert.Contains("context", wide);
        Assert.DoesNotContain("model", medium);
        Assert.DoesNotContain("in/out", medium);
        Assert.Contains("turns", medium);
        Assert.Contains("context", medium);
        Assert.DoesNotContain("turns", narrow);
        Assert.DoesNotContain("context", narrow);
    }

    [Fact]
    public void ActivityHintShowsLivePhases()
    {
        var session = CreateSession("test", "running");
        var reasoning = CreateLive("reasoning", reasoning: "hello\nworld", reasoningCharacters: 11, phaseStartedAt: Now.AddSeconds(-14));
        var answering = CreateLive("answering", text: "hello", textCharacters: 5);
        var runningTool = CreateLive("running_tools", activeTool: new MonitorActiveTool("call_1", "shell", "ls", Now.AddSeconds(-12)));
        var calling = CreateLive("turn_done", proposed: [new MonitorProposedCall("call_1", "shell", "ls")]);
        var waiting = CreateLive("waiting_for_model", phaseStartedAt: Now.AddSeconds(-3));

        Assert.Equal("thinking 11: hello world", ActivityHint.Format(session, reasoning, Now, 80));
        Assert.Equal("writing 5: hello", ActivityHint.Format(session, answering, Now, 80));
        Assert.Equal("running shell 12s: ls", ActivityHint.Format(session, runningTool, Now, 80));
        Assert.Equal("calling shell: ls", ActivityHint.Format(session, calling, Now, 80));
        Assert.Equal("waiting for model 3s", ActivityHint.Format(session, waiting, Now, 80));
    }

    [Fact]
    public void ActivityHintCollapsesWhitespaceAndKeepsTheEndOfTheTail()
    {
        var session = CreateSession("test", "running");
        var live = CreateLive("reasoning", reasoning: "a\n\n b", reasoningCharacters: 5);
        var longTail = CreateLive("reasoning", reasoning: "0123456789", reasoningCharacters: 10);

        Assert.Equal("thinking 5: a b", ActivityHint.Format(session, live, Now, 80));
        Assert.Equal("thinking 10: 3456789", ActivityHint.Format(session, longTail, Now, 20));
    }

    [Fact]
    public void ActivityHintFallsBackToTheFiles()
    {
        var running = CreateSession("test", "running", lastActivity: "waiting for the model");
        var approval = CreateSession("test", "approval", pendingCount: 2, needsHumanCount: 1);
        var finished = CreateSession("test", "finished", updatedAt: Now.AddMinutes(-12));
        var failed = CreateSession("test", "failed", errorMessage: "boom");
        var stopped = CreateSession("test", "stopped", updatedAt: Now.AddMinutes(-3));

        Assert.Equal("waiting for the model", ActivityHint.Format(running, null, Now, 80));
        Assert.Equal("2 pending (1 needs a human)", ActivityHint.Format(approval, null, Now, 80));
        Assert.Equal("finished 12m ago", ActivityHint.Format(finished, null, Now, 80));
        Assert.Equal("failed: boom", ActivityHint.Format(failed, null, Now, 80));
        Assert.Equal("stopped 3m ago", ActivityHint.Format(stopped, null, Now, 80));
    }

    [Fact]
    public void DetailHeaderMatchesTheExample()
    {
        var session = CreateSession(
            "review-parser",
            "running",
            workdir: @"D:\repos\app",
            maxTurns: 100,
            currentRun: CreateRun(12, 31, 182_000, 141_000, 9_800, Now.AddSeconds(-192)),
            sessionCounters: new CountersRecord { ModelCalls = 12, ToolCalls = 31, InputTokens = 182_000, CachedInputTokens = 141_000, OutputTokens = 9_800 },
            pendingCount: 1,
            needsHumanCount: 0,
            contextTokens: 23_400,
            updatedAt: Now,
            elapsedSeconds: 192,
            processId: 18_324,
            statusUpdatedAt: Now.AddSeconds(-2),
            sessionStartedAt: Now.AddSeconds(-845));
        var live = CreateLive("turn_done", lastTurn: new MonitorLastTurn(12.3, 1_100));

        var lines = DetailHeaderFormatter.Format(session, live, 128_000, Now);

        Assert.Equal(
            [
                "review-parser  running  live",
                "model    deepseek-high (deepseek-v4-1-flash-1017)  shell bash  policy readonly",
                @"workdir  D:\repos\app  run 0002-20261117-101500 (resume)  pid 18324  last activity 2s ago",
                "time     run 3m12s  session 14m05s  turns 12 of 100 (88 left)  tools 31  pending 1 (0 need a human)",
                "tokens   in 182k (uncached 41.0k, cached 141k, 77% cached)  out 9.8k",
                "context  23.4k of 128k [####................] 18%  last turn 12.3s, 1.1k out, 89 tok/s"
            ],
            lines);
    }

    [Fact]
    public void DetailHeaderShowsContextWithoutAWindowAndOmitsSlowTokensPerSecond()
    {
        var session = CreateSession("test", "running", contextTokens: 23_400, currentRun: CreateRun(0, 0, 0, 0, 0, Now));
        var live = CreateLive("turn_done", lastTurn: new MonitorLastTurn(0.4, 100));

        var withoutWindow = DetailHeaderFormatter.Format(session, null, null, Now);
        var slowTurn = DetailHeaderFormatter.Format(session, live, 128_000, Now);

        Assert.Equal("context  23.4k", withoutWindow[^1]);
        Assert.EndsWith("last turn 0.4s, 100 out", slowTurn[^1]);
        Assert.DoesNotContain("tok/s", slowTurn[^1]);
    }

    [Fact]
    public void AFinishedRunIsTimedToItsEndAndAnUnknownContextIsMarked()
    {
        var started = Now.AddDays(-2);
        var session = CreateSession(
            "old",
            "finished",
            currentRun: CreateRun(0, 0, 0, 0, 0, started, started.AddSeconds(151)),
            updatedAt: started.AddSeconds(151),
            sessionStartedAt: started);

        var lines = DetailHeaderFormatter.Format(session, null, 128_000, Now);
        var row = SessionRowFormatter.FormatRow(session, false, string.Empty, 0, 120, Now);

        Assert.StartsWith("time     run 2m31s  session 2m31s", lines[3]);
        Assert.Equal("context  not recorded yet", lines[5]);
        Assert.Contains(" 2m31s ", row);
        Assert.DoesNotContain("2d", row);
    }

    [Fact]
    public void DetailHeaderHandlesZeroInputTokens()
    {
        var session = CreateSession("test", "running", currentRun: CreateRun(0, 0, 0, 0, 0, Now));

        var lines = DetailHeaderFormatter.Format(session, null, null, Now);

        Assert.Contains("0% cached", lines[4]);
    }

    private static SessionSummary CreateSession(
        string name,
        string displayState,
        string modelAlias = "deepseek-high",
        string modelId = "deepseek-v4-1-flash-1017",
        string workdir = @"D:\repos\app",
        IReadOnlyList<string>? policy = null,
        string shellKind = "bash",
        int maxTurns = 100,
        CountersRecord? sessionCounters = null,
        RunSummary? currentRun = null,
        int pendingCount = 0,
        int needsHumanCount = 0,
        long contextTokens = 0,
        string? errorMessage = null,
        DateTimeOffset? updatedAt = null,
        double? elapsedSeconds = null,
        string? lastActivity = null,
        int? processId = null,
        DateTimeOffset? statusUpdatedAt = null,
        DateTimeOffset? sessionStartedAt = null)
    {
        return new SessionSummary
        {
            Directory = Path.Combine(Path.GetTempPath(), name),
            Name = name,
            Workdir = workdir,
            DisplayState = displayState,
            ModelAlias = modelAlias,
            ModelId = modelId,
            Policy = policy ?? ["readonly"],
            ShellKind = shellKind,
            MaxTurns = maxTurns,
            SessionCounters = sessionCounters ?? new CountersRecord(),
            CurrentRun = currentRun,
            PendingCount = pendingCount,
            NeedsHumanCount = needsHumanCount,
            ContextTokens = contextTokens,
            ErrorMessage = errorMessage,
            UpdatedAt = updatedAt ?? Now,
            SessionStartedAt = sessionStartedAt ?? Now,
            HasFinalText = false,
            ElapsedSeconds = elapsedSeconds,
            TurnsRemaining = null,
            LastActivity = lastActivity,
            ProcessId = processId,
            StatusUpdatedAt = statusUpdatedAt
        };
    }

    private static RunSummary CreateRun(int modelCalls, int toolCalls, long input, long cached, long output, DateTimeOffset startedAt, DateTimeOffset? endedAt = null)
    {
        return new RunSummary
        {
            RunId = "0002-20261117-101500",
            Kind = "resume",
            StartedAt = startedAt,
            EndedAt = endedAt,
            Counters = new CountersRecord
            {
                ModelCalls = modelCalls,
                ToolCalls = toolCalls,
                InputTokens = input,
                CachedInputTokens = cached,
                OutputTokens = output
            }
        };
    }

    private static LiveSessionSnapshot CreateLive(
        string phase,
        string reasoning = "",
        long reasoningCharacters = 0,
        string text = "",
        long textCharacters = 0,
        IReadOnlyList<MonitorProposedCall>? proposed = null,
        MonitorActiveTool? activeTool = null,
        MonitorLastTurn? lastTurn = null,
        DateTimeOffset? phaseStartedAt = null)
    {
        var snapshot = new MonitorSnapshot(
            phase,
            phaseStartedAt ?? Now,
            1,
            reasoning,
            reasoningCharacters,
            text,
            textCharacters,
            proposed ?? [],
            activeTool,
            null,
            0,
            lastTurn);
        return new LiveSessionSnapshot(snapshot, true, Now, null, null);
    }

    private static string Pad(string value, int width)
    {
        return value.PadRight(width);
    }

    private static string Join(params string[] cells)
    {
        return string.Join(" ", cells).TrimEnd();
    }
}

using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Monitoring;
using Subordinate.Tui.Live;
using Subordinate.Tui.Presentation;

namespace Subordinate.Tests;

public class HistoryFormatterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void FormatsRolesAsCards()
    {
        var state = new HistoryFormatState();
        var system = SessionHarness.Text(ChatRole.System, "You are a test subagent.");
        var task = SessionHarness.Text(ChatRole.User, "Do the task.");
        var reasoning = new ChatMessage { Role = ChatRole.Assistant, Contents = [new ReasoningContent { Text = "thinking hard" }] };
        var supervisor = SessionHarness.Text(ChatRole.User, "continue");
        var final = SessionHarness.Text(ChatRole.Assistant, "done");

        Assert.Equal(
            [
                new HistoryBlock(HistoryBlockKind.System, HistoryBlockRole.Spacer, string.Empty),
                new HistoryBlock(HistoryBlockKind.System, HistoryBlockRole.Note, $"system prompt, {system.GetText().Length} characters, hidden")
            ],
            HistoryFormatter.FormatMessage(system, state, false, false));
        Assert.Equal(
            [
                new HistoryBlock(HistoryBlockKind.Task, HistoryBlockRole.Spacer, string.Empty),
                new HistoryBlock(HistoryBlockKind.Task, HistoryBlockRole.Header, "task"),
                new HistoryBlock(HistoryBlockKind.Task, HistoryBlockRole.Body, "Do the task.")
            ],
            HistoryFormatter.FormatMessage(task, state, false, false));

        var collapsed = HistoryFormatter.FormatMessage(reasoning, state, false, false);
        Assert.Equal(new HistoryBlock(HistoryBlockKind.Thinking, HistoryBlockRole.Note, "thinking, 13 characters (r to show)"), collapsed[^1]);

        var expanded = HistoryFormatter.FormatMessage(reasoning, new HistoryFormatState(), true, false);
        Assert.Equal(new HistoryBlock(HistoryBlockKind.Thinking, HistoryBlockRole.Header, "thinking"), expanded[1]);
        Assert.Equal(new HistoryBlock(HistoryBlockKind.Thinking, HistoryBlockRole.Body, "thinking hard"), expanded[2]);

        Assert.Equal(new HistoryBlock(HistoryBlockKind.Supervisor, HistoryBlockRole.Header, "supervisor"), HistoryFormatter.FormatMessage(supervisor, state, false, false)[1]);
        Assert.Equal(new HistoryBlock(HistoryBlockKind.Assistant, HistoryBlockRole.Body, "done"), HistoryFormatter.FormatMessage(final, state, false, false)[^1]);
    }

    [Fact]
    public void CollapsedThinkingAttachesToTheNextCard()
    {
        var state = new HistoryFormatState();
        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Contents = [new ReasoningContent { Text = "plan" }, Call("call_1", "ls")]
        };

        var blocks = HistoryFormatter.FormatMessage(message, state, false, false);

        Assert.Equal(
            [HistoryBlockRole.Spacer, HistoryBlockRole.Note, HistoryBlockRole.Header],
            blocks.Select(block => block.Role));
        Assert.Equal("shell  ls", blocks[2].Text);
    }

    [Fact]
    public void AShellResultRightAfterItsCallShowsStatusAndOutput()
    {
        var state = new HistoryFormatState();
        HistoryFormatter.FormatMessage(new ChatMessage { Role = ChatRole.Assistant, Contents = [Call("call_1", "ls")] }, state, false, false);
        var output = "exit_code: 0\nduration_seconds: 0.1\nstdout:\n" + string.Join("\n", Enumerable.Range(1, 20).Select(index => "line " + index));

        var collapsed = HistoryFormatter.FormatMessage(Result("call_1", output), state, false, false);

        Assert.Equal(2, collapsed.Count);
        Assert.Equal(new HistoryBlock(HistoryBlockKind.ToolResult, HistoryBlockRole.Note, "ok  exit 0  0.1s"), collapsed[0]);
        Assert.Equal(HistoryFormatter.CollapsedOutputLines + 1, collapsed[1].Text.Split('\n').Length);
        Assert.StartsWith("line 1\n", collapsed[1].Text);
        Assert.EndsWith("... 12 more lines", collapsed[1].Text);
    }

    [Fact]
    public void ParallelResultsRepeatTheirCallHeader()
    {
        var state = new HistoryFormatState();
        HistoryFormatter.FormatMessage(new ChatMessage { Role = ChatRole.Assistant, Contents = [Call("call_1", "ls"), Call("call_2", "pwd")] }, state, false, false);

        var first = HistoryFormatter.FormatMessage(Result("call_1", "exit_code: 2\nduration_seconds: 0.3\nstderr:\nno such file"), state, false, false);

        Assert.Equal(new HistoryBlock(HistoryBlockKind.Tool, HistoryBlockRole.Header, "shell  ls"), first[1]);
        Assert.Equal(new HistoryBlock(HistoryBlockKind.ToolFailure, HistoryBlockRole.Note, "failed  exit 2  0.3s"), first[2]);
        Assert.Equal("stderr:\nno such file", first[3].Text);
    }

    [Fact]
    public void TimeoutsPendingAndDeniedResultsAreMarked()
    {
        var state = new HistoryFormatState();

        var timedOut = HistoryFormatter.FormatMessage(Result("call_1", "The command timed out after 30 seconds and was killed.\nduration_seconds: 30.0"), state, false, false);
        var pending = HistoryFormatter.FormatMessage(Result("call_2", "PENDING APPROVAL abc: this call was not executed. Why: write. The supervisor will decide later."), state, false, false);
        var denied = HistoryFormatter.FormatMessage(Result("call_3", "DENIED abc: the supervisor denied this exact call. Do not retry it."), state, false, false);

        Assert.Equal(new HistoryBlock(HistoryBlockKind.ToolFailure, HistoryBlockRole.Note, "The command timed out after 30 seconds and was killed.  30.0s  no output"), timedOut[^1]);
        Assert.Equal(new HistoryBlock(HistoryBlockKind.Warning, HistoryBlockRole.Note, "PENDING APPROVAL abc: this call was not executed. Why: write."), pending[^1]);
        Assert.Equal(new HistoryBlock(HistoryBlockKind.Warning, HistoryBlockRole.Note, "DENIED abc: the supervisor denied this exact call."), denied[^1]);
    }

    [Fact]
    public void TheLiveSectionShowsTheLastTwelveLinesAndDisappearsWhenEnded()
    {
        var reasoningText = string.Join("\n", Enumerable.Range(1, 20).Select(index => "thought " + index));
        var reasoning = new LiveSessionSnapshot(
            new MonitorSnapshot("reasoning", Now.AddSeconds(-14), 1, reasoningText, reasoningText.Length, string.Empty, 0, [], null, null, 0, null),
            true,
            Now,
            null,
            null);

        var blocks = HistoryFormatter.FormatLive(reasoning, Now, 40);

        Assert.Equal(new HistoryBlock(HistoryBlockKind.Live, HistoryBlockRole.Header, $"live  thinking {Units.FormatTokens(reasoningText.Length)} characters, 14s"), blocks[1]);
        var lines = blocks[2].Text.Split('\n');
        Assert.Equal(HistoryFormatter.LiveTailLines, lines.Length);
        Assert.Equal("thought 20", lines[^1]);

        var ended = new LiveSessionSnapshot(
            new MonitorSnapshot("ended", Now, 1, string.Empty, 0, string.Empty, 0, [], null, null, 0, null),
            true,
            Now,
            "finished",
            null);
        Assert.Empty(HistoryFormatter.FormatLive(ended, Now, 40));
    }

    [Fact]
    public void EscapeCharactersInToolResultsAreSanitized()
    {
        var block = HistoryFormatter.FormatMessage(Result("call_1", "a\u001b[31mb"), new HistoryFormatState(), false, false)[^1];

        Assert.Contains("a^[[31mb", block.Text);
        Assert.DoesNotContain('\u001b', block.Text);
    }

    [Fact]
    public void PlainLayoutDrawsHeadersAsRulesAndIndentsBodies()
    {
        var blocks = new[]
        {
            new HistoryBlock(HistoryBlockKind.Task, HistoryBlockRole.Spacer, string.Empty),
            new HistoryBlock(HistoryBlockKind.Task, HistoryBlockRole.Header, "task"),
            new HistoryBlock(HistoryBlockKind.Task, HistoryBlockRole.Body, "Do it.")
        };
        var plain = new List<HistoryLine>();
        var colored = new List<HistoryLine>();

        HistoryLayout.Append(blocks, 20, true, plain);
        HistoryLayout.Append(blocks, 20, false, colored);

        Assert.Equal([string.Empty, "-- task ------------", "  Do it."], plain.Select(line => line.Text));
        Assert.Equal([string.Empty, " task", "  Do it."], colored.Select(line => line.Text));
    }

    private static ToolCallContent Call(string callId, string command)
    {
        return new ToolCallContent
        {
            CallId = callId,
            ToolName = "shell",
            Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["command"] = command })
        };
    }

    private static ChatMessage Result(string callId, string output)
    {
        return new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents = [new ToolResultContent { CallId = callId, ToolName = "shell", Output = output }]
        };
    }
}

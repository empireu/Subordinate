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
    public void FormatsTranscriptMessages()
    {
        var system = SessionHarness.Text(ChatRole.System, "You are a test subagent.");
        var task = SessionHarness.Text(ChatRole.User, "Do the task.");
        var reasoning = new ChatMessage { Role = ChatRole.Assistant, Contents = [new ReasoningContent { Text = "thinking hard" }] };
        var answer = new ChatMessage { Role = ChatRole.Assistant, Contents = [new TextContent { Text = "answer" }] };
        var call = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new ToolCallContent
                {
                    CallId = "call_1",
                    ToolName = "shell",
                    Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["command"] = "ls" })
                }
            ]
        };
        var output = string.Join("\n", Enumerable.Range(1, 20).Select(index => "line " + index));
        var result = new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents = [new ToolResultContent { CallId = "call_1", ToolName = "shell", Output = output }]
        };
        var deferred = new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents = [new ToolResultContent { CallId = "call_2", ToolName = "write_file", Output = "PENDING APPROVAL abc: this call was not executed." }]
        };
        var supervisor = SessionHarness.Text(ChatRole.User, "continue");
        var final = SessionHarness.Text(ChatRole.Assistant, "done");

        var systemBlock = Assert.Single(HistoryFormatter.FormatMessage(system, false, false, false));
        Assert.Equal(HistoryBlockKind.System, systemBlock.Kind);
        Assert.Equal($"[system] {system.GetText().Length} characters, hidden", systemBlock.Text);

        var taskBlock = Assert.Single(HistoryFormatter.FormatMessage(task, true, false, false));
        Assert.Equal(HistoryBlockKind.Task, taskBlock.Kind);
        Assert.Equal("[task]\nDo the task.", taskBlock.Text);

        var collapsedReasoning = Assert.Single(HistoryFormatter.FormatMessage(reasoning, false, false, false));
        Assert.Equal("[thinking] 13 characters (r to show)", collapsedReasoning.Text);
        var expandedReasoning = Assert.Single(HistoryFormatter.FormatMessage(reasoning, false, true, false));
        Assert.Equal("[thinking]\nthinking hard", expandedReasoning.Text);

        var answerBlock = Assert.Single(HistoryFormatter.FormatMessage(answer, false, false, false));
        Assert.Equal(HistoryBlockKind.Assistant, answerBlock.Kind);
        Assert.Equal("[assistant]\nanswer", answerBlock.Text);

        var callBlock = Assert.Single(HistoryFormatter.FormatMessage(call, false, false, false));
        Assert.Equal(HistoryBlockKind.Tool, callBlock.Kind);
        Assert.Equal("[shell] ls", callBlock.Text);

        var collapsedResult = Assert.Single(HistoryFormatter.FormatMessage(result, false, false, false));
        Assert.Equal(HistoryBlockKind.ToolResult, collapsedResult.Kind);
        Assert.Equal(9, collapsedResult.Text.Split('\n').Length);
        Assert.StartsWith("line 1\n", collapsedResult.Text);
        Assert.EndsWith("... 12 more lines", collapsedResult.Text);
        Assert.DoesNotContain("line 9", collapsedResult.Text);

        var expandedResult = Assert.Single(HistoryFormatter.FormatMessage(result, false, false, true));
        Assert.Equal(output, expandedResult.Text);

        var deferredBlock = Assert.Single(HistoryFormatter.FormatMessage(deferred, false, false, false));
        Assert.Equal(HistoryBlockKind.Warning, deferredBlock.Kind);

        var supervisorBlock = Assert.Single(HistoryFormatter.FormatMessage(supervisor, false, false, false));
        Assert.Equal(HistoryBlockKind.Supervisor, supervisorBlock.Kind);
        Assert.Equal("[supervisor]\ncontinue", supervisorBlock.Text);

        var finalBlock = Assert.Single(HistoryFormatter.FormatMessage(final, false, false, false));
        Assert.Equal("[assistant]\ndone", finalBlock.Text);
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

        var block = Assert.Single(HistoryFormatter.FormatLive(reasoning, Now, 40));

        Assert.Equal(HistoryBlockKind.Live, block.Kind);
        Assert.StartsWith($"[live] thinking {Units.FormatTokens(reasoningText.Length)} characters, 14s", block.Text);
        var lines = block.Text.Split('\n');
        Assert.Equal(13, lines.Length);
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
        var result = new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents = [new ToolResultContent { CallId = "call_1", ToolName = "shell", Output = "a\u001b[31mb" }]
        };

        var block = Assert.Single(HistoryFormatter.FormatMessage(result, false, false, false));

        Assert.Contains("a^[[31mb", block.Text);
        Assert.DoesNotContain('\u001b', block.Text);
    }
}

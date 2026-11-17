using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Running;
using Subordinate.Tui.Live;

namespace Subordinate.Tui.Presentation;

public enum HistoryBlockKind
{
    System,
    Task,
    Supervisor,
    Thinking,
    Assistant,
    Tool,
    ToolResult,
    Warning,
    Live
}

public sealed record HistoryBlock(HistoryBlockKind Kind, string Text);

public static class HistoryFormatter
{
    public static IReadOnlyList<HistoryBlock> FormatMessage(ChatMessage message, bool isFirstUser, bool showReasoning, bool expandOutputs)
    {
        var blocks = new List<HistoryBlock>();

        switch (message.Role)
        {
            case ChatRole.System:
            {
                var text = message.GetText();
                blocks.Add(new HistoryBlock(HistoryBlockKind.System, $"[system] {text.Length} characters, hidden"));
                break;
            }
            case ChatRole.User:
            {
                var text = TerminalText.Sanitize(message.GetText());
                blocks.Add(new HistoryBlock(isFirstUser ? HistoryBlockKind.Task : HistoryBlockKind.Supervisor, (isFirstUser ? "[task]" : "[supervisor]") + "\n" + text));
                break;
            }
            case ChatRole.Assistant:
            {
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case ReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        {
                            if (showReasoning)
                            {
                                blocks.Add(new HistoryBlock(HistoryBlockKind.Thinking, "[thinking]\n" + TerminalText.Sanitize(reasoning.Text)));
                            }
                            else
                            {
                                blocks.Add(new HistoryBlock(HistoryBlockKind.Thinking, $"[thinking] {reasoning.Text.Length} characters (r to show)"));
                            }

                            break;
                        }
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                        {
                            blocks.Add(new HistoryBlock(HistoryBlockKind.Assistant, "[assistant]\n" + TerminalText.Sanitize(text.Text)));
                            break;
                        }
                        case ToolCallContent call:
                        {
                            var summary = TerminalText.Sanitize(ToolCallSummary.Describe(call));
                            blocks.Add(new HistoryBlock(HistoryBlockKind.Tool, $"[{TerminalText.Sanitize(call.ToolName)}] {summary}"));
                            break;
                        }
                    }
                }

                break;
            }
            case ChatRole.Tool:
            {
                foreach (var content in message.Contents.OfType<ToolResultContent>())
                {
                    var output = TerminalText.Sanitize(content.Output);
                    var lines = output.Split('\n');
                    var limit = expandOutputs ? 200 : 8;
                    var text = string.Join("\n", lines.Take(limit));

                    if (lines.Length > limit)
                    {
                        text += $"\n... {lines.Length - limit} more lines";
                    }

                    var kind = output.StartsWith("PENDING APPROVAL", StringComparison.Ordinal) || output.StartsWith("DENIED", StringComparison.Ordinal)
                        ? HistoryBlockKind.Warning
                        : HistoryBlockKind.ToolResult;
                    blocks.Add(new HistoryBlock(kind, text));
                }

                break;
            }
        }

        return blocks;
    }

    public static IReadOnlyList<HistoryBlock> FormatLive(LiveSessionSnapshot? live, DateTimeOffset now, int width)
    {
        if (live == null || live.State.Phase == "ended")
        {
            return [];
        }

        var state = live.State;
        var blocks = new List<HistoryBlock>();

        switch (state.Phase)
        {
            case "reasoning":
            {
                var duration = Units.FormatDuration((now - state.PhaseStartedAt).TotalSeconds);
                var header = $"[live] thinking {Units.FormatTokens(state.ReasoningCharacters)} characters, {duration}";
                blocks.Add(new HistoryBlock(HistoryBlockKind.Live, header + "\n" + TailLines(state.Reasoning, width, 12)));
                break;
            }
            case "answering":
            {
                blocks.Add(new HistoryBlock(HistoryBlockKind.Live, "[live] writing\n" + TailLines(state.Text, width, 12)));
                break;
            }
            case "running_tools" when state.ActiveTool != null:
            {
                var duration = Units.FormatDuration((now - state.ActiveTool.StartedAt).TotalSeconds);
                blocks.Add(new HistoryBlock(HistoryBlockKind.Live, $"[live] running {TerminalText.Sanitize(state.ActiveTool.Tool)} {duration}: {TerminalText.Sanitize(state.ActiveTool.Summary)}"));
                break;
            }
            case "waiting_for_model":
            {
                var duration = Units.FormatDuration((now - state.PhaseStartedAt).TotalSeconds);
                blocks.Add(new HistoryBlock(HistoryBlockKind.Live, $"[live] waiting for model {duration}"));
                break;
            }
            case "starting":
            {
                blocks.Add(new HistoryBlock(HistoryBlockKind.Live, "[live] starting"));
                break;
            }
        }

        return blocks;
    }

    private static string TailLines(string text, int width, int count)
    {
        var sanitized = TerminalText.Sanitize(text);
        var lines = TextWrap.Wrap(sanitized, width, 0);
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - count)));
    }
}

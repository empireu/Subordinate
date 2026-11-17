using System.Globalization;
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
    ToolFailure,
    Warning,
    Live
}

public enum HistoryBlockRole
{
    Header,
    Body,
    Note,
    Spacer
}

public sealed record HistoryBlock(HistoryBlockKind Kind, HistoryBlockRole Role, string Text);

/// <summary>
///     Carries what earlier messages established, so a tool result can be paired with its call.
/// </summary>
public sealed class HistoryFormatState
{
    internal Dictionary<string, (string Tool, string Summary)> Calls { get; } = new(StringComparer.Ordinal);

    internal bool SeenUser { get; set; }

    internal string? LastCallHeaderId { get; set; }

    internal bool AttachNextHeader { get; set; }

    public void Reset()
    {
        Calls.Clear();
        SeenUser = false;
        LastCallHeaderId = null;
        AttachNextHeader = false;
    }
}

public static class HistoryFormatter
{
    public const int CollapsedOutputLines = 8;
    public const int ExpandedOutputLines = 200;
    public const int LiveTailLines = 12;
    private static readonly char[] LineBreaks = ['\n', '\r'];

    public static IReadOnlyList<HistoryBlock> FormatMessage(ChatMessage message, HistoryFormatState state, bool showReasoning, bool expandOutputs)
    {
        var blocks = new List<HistoryBlock>();

        switch (message.Role)
        {
            case ChatRole.System:
            {
                AddNote(blocks, state, HistoryBlockKind.System, $"system prompt, {message.GetText().Length} characters, hidden");
                break;
            }
            case ChatRole.User:
            {
                var isTask = !state.SeenUser;
                state.SeenUser = true;
                AddCard(blocks, state, isTask ? HistoryBlockKind.Task : HistoryBlockKind.Supervisor, isTask ? "task" : "supervisor", TerminalText.Sanitize(message.GetText()));
                break;
            }
            case ChatRole.Assistant:
            {
                FormatAssistant(message, state, showReasoning, blocks);
                break;
            }
            case ChatRole.Tool:
            {
                foreach (var content in message.Contents.OfType<ToolResultContent>())
                {
                    FormatToolResult(content, state, expandOutputs, blocks);
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
        var bodyWidth = Math.Max(1, width - HistoryLayout.BodyIndent);
        var blocks = new List<HistoryBlock>();

        switch (state.Phase)
        {
            case "reasoning":
            {
                var duration = Units.FormatDuration((now - state.PhaseStartedAt).TotalSeconds);
                AddLive(blocks, $"live  thinking {Units.FormatTokens(state.ReasoningCharacters)} characters, {duration}", TailLines(state.Reasoning, bodyWidth, LiveTailLines));
                break;
            }
            case "answering":
            {
                AddLive(blocks, "live  writing", TailLines(state.Text, bodyWidth, LiveTailLines));
                break;
            }
            case "running_tools" when state.ActiveTool != null:
            {
                var duration = Units.FormatDuration((now - state.ActiveTool.StartedAt).TotalSeconds);
                AddLive(blocks, $"live  running {TerminalText.Sanitize(state.ActiveTool.Tool)} {duration}", TerminalText.Sanitize(state.ActiveTool.Summary));
                break;
            }
            case "waiting_for_model":
            {
                var duration = Units.FormatDuration((now - state.PhaseStartedAt).TotalSeconds);
                AddLive(blocks, $"live  waiting for the model {duration}", null);
                break;
            }
            case "starting":
            {
                AddLive(blocks, "live  starting", null);
                break;
            }
        }

        return blocks;
    }

    private static void FormatAssistant(ChatMessage message, HistoryFormatState state, bool showReasoning, List<HistoryBlock> blocks)
    {
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case ReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                {
                    if (showReasoning)
                    {
                        AddCard(blocks, state, HistoryBlockKind.Thinking, "thinking", TerminalText.Sanitize(reasoning.Text));
                    }
                    else
                    {
                        AddNote(blocks, state, HistoryBlockKind.Thinking, $"thinking, {reasoning.Text.Length} characters (r to show)");
                        state.AttachNextHeader = true;
                    }

                    break;
                }
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                {
                    AddCard(blocks, state, HistoryBlockKind.Assistant, "assistant", TerminalText.Sanitize(text.Text));
                    break;
                }
                case ToolCallContent call:
                {
                    var tool = TerminalText.Sanitize(call.ToolName);
                    var summary = TerminalText.Sanitize(ToolCallSummary.Describe(call));
                    state.Calls[call.CallId] = (tool, summary);
                    AddCard(blocks, state, HistoryBlockKind.Tool, ToolHeader(tool, summary), null);
                    state.LastCallHeaderId = call.CallId;
                    state.AttachNextHeader = true;
                    break;
                }
            }
        }

        state.AttachNextHeader = false;
    }

    private static void FormatToolResult(ToolResultContent content, HistoryFormatState state, bool expandOutputs, List<HistoryBlock> blocks)
    {
        if (state.LastCallHeaderId != content.CallId)
        {
            var (tool, summary) = state.Calls.TryGetValue(content.CallId, out var call) ? call : (TerminalText.Sanitize(content.ToolName), string.Empty);
            AddCard(blocks, state, HistoryBlockKind.Tool, ToolHeader(tool, summary), null);
        }

        state.LastCallHeaderId = null;
        var output = TerminalText.Sanitize(content.Output);

        if (output.StartsWith("PENDING APPROVAL", StringComparison.Ordinal))
        {
            blocks.Add(new HistoryBlock(HistoryBlockKind.Warning, HistoryBlockRole.Note, CutAt(output, " The supervisor will decide")));
            return;
        }

        if (output.StartsWith("DENIED", StringComparison.Ordinal))
        {
            blocks.Add(new HistoryBlock(HistoryBlockKind.Warning, HistoryBlockRole.Note, CutAt(output, " Do not retry")));
            return;
        }

        string? timeout = null;

        if (output.StartsWith("The command timed out", StringComparison.Ordinal))
        {
            var newline = output.IndexOf('\n');
            timeout = newline < 0 ? output : output[..newline];
            output = newline < 0 ? string.Empty : output[(newline + 1)..];
        }

        var parsed = ParseShellOutput(output);
        var failed = content.IsError || timeout != null || parsed.ExitCode is not (null or 0);

        if (timeout != null || parsed.ExitCode.HasValue || parsed.Duration != null || content.IsError)
        {
            var status = timeout ?? (failed ? "failed" : "ok");

            if (parsed.ExitCode.HasValue)
            {
                status += $"  exit {parsed.ExitCode.Value.ToString(CultureInfo.InvariantCulture)}";
            }

            if (parsed.Duration != null)
            {
                status += $"  {parsed.Duration}s";
            }

            if (parsed.Body.Length == 0)
            {
                status += "  no output";
            }

            blocks.Add(new HistoryBlock(failed ? HistoryBlockKind.ToolFailure : HistoryBlockKind.ToolResult, HistoryBlockRole.Note, status));
        }
        else if (parsed.Body.Length == 0)
        {
            blocks.Add(new HistoryBlock(HistoryBlockKind.ToolResult, HistoryBlockRole.Note, "no output"));
        }

        if (parsed.Body.Length == 0)
        {
            return;
        }

        var lines = parsed.Body.Split('\n');
        var limit = expandOutputs ? ExpandedOutputLines : CollapsedOutputLines;
        var text = string.Join("\n", lines.Take(limit));

        if (lines.Length > limit)
        {
            text += $"\n... {lines.Length - limit} more lines";
        }

        blocks.Add(new HistoryBlock(HistoryBlockKind.ToolResult, HistoryBlockRole.Body, text));
    }

    private static (int? ExitCode, string? Duration, string Body) ParseShellOutput(string output)
    {
        var lines = output.Split('\n');
        var index = 0;
        int? exitCode = null;
        string? duration = null;

        if (index < lines.Length && lines[index].StartsWith("exit_code: ", StringComparison.Ordinal)
            && int.TryParse(lines[index]["exit_code: ".Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
        {
            exitCode = code;
            index++;
        }

        if (index < lines.Length && lines[index].StartsWith("duration_seconds: ", StringComparison.Ordinal))
        {
            duration = lines[index]["duration_seconds: ".Length..].Trim();
            index++;
        }

        if (exitCode == null && duration == null)
        {
            return (null, null, output.TrimEnd('\n'));
        }

        if (index < lines.Length && lines[index] == "stdout:")
        {
            index++;
        }

        return (exitCode, duration, string.Join("\n", lines.Skip(index)).TrimEnd('\n'));
    }

    private static void AddCard(List<HistoryBlock> blocks, HistoryFormatState state, HistoryBlockKind kind, string header, string? body)
    {
        if (!state.AttachNextHeader)
        {
            blocks.Add(new HistoryBlock(kind, HistoryBlockRole.Spacer, string.Empty));
        }

        state.AttachNextHeader = false;
        state.LastCallHeaderId = null;
        blocks.Add(new HistoryBlock(kind, HistoryBlockRole.Header, header));
        body = body?.TrimEnd();

        if (!string.IsNullOrEmpty(body))
        {
            blocks.Add(new HistoryBlock(kind, HistoryBlockRole.Body, body));
        }
    }

    private static void AddNote(List<HistoryBlock> blocks, HistoryFormatState state, HistoryBlockKind kind, string text)
    {
        blocks.Add(new HistoryBlock(kind, HistoryBlockRole.Spacer, string.Empty));
        blocks.Add(new HistoryBlock(kind, HistoryBlockRole.Note, text));
        state.AttachNextHeader = false;
        state.LastCallHeaderId = null;
    }

    private static void AddLive(List<HistoryBlock> blocks, string header, string? body)
    {
        blocks.Add(new HistoryBlock(HistoryBlockKind.Live, HistoryBlockRole.Spacer, string.Empty));
        blocks.Add(new HistoryBlock(HistoryBlockKind.Live, HistoryBlockRole.Header, header));

        if (!string.IsNullOrEmpty(body))
        {
            blocks.Add(new HistoryBlock(HistoryBlockKind.Live, HistoryBlockRole.Body, body));
        }
    }

    private static string ToolHeader(string tool, string summary)
    {
        return summary.Length == 0 ? tool : $"{tool}  {summary}";
    }

    private static string CutAt(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        return index > 0 ? text[..index] : text;
    }

    /// <summary>
    ///     Wraps only the paragraphs that can reach the last lines, so the breaks match wrapping the whole text.
    /// </summary>
    internal static string TailLines(string text, int width, int count)
    {
        var needed = (count + 1) * Math.Max(10, width);
        var start = 0;

        if (text.Length > needed)
        {
            start = text.LastIndexOfAny(LineBreaks, text.Length - needed) + 1;
        }

        var sanitized = TerminalText.Sanitize(start == 0 ? text : text[start..]);
        var lines = TextWrap.Wrap(sanitized, width, 0);
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - count)));
    }
}

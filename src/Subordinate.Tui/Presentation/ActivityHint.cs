using System.Text;
using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;
using Subordinate.Tui.Live;

namespace Subordinate.Tui.Presentation;

public static class ActivityHint
{
    public static string Format(SessionSummary session, LiveSessionSnapshot? live, DateTimeOffset now, int width)
    {
        if (live != null)
        {
            var hint = FormatLive(live, now, width);

            if (hint != null)
            {
                return hint;
            }
        }

        return FormatFile(session, now);
    }

    private static string? FormatLive(LiveSessionSnapshot live, DateTimeOffset now, int width)
    {
        var state = live.State;

        switch (state.Phase)
        {
            case "waiting_for_model":
            {
                return $"waiting for model {Units.FormatDuration((now - state.PhaseStartedAt).TotalSeconds)}";
            }
            case "reasoning":
            {
                return WithTail($"thinking {Units.FormatTokens(state.ReasoningCharacters)}: ", state.Reasoning, width);
            }
            case "answering":
            {
                return WithTail($"writing {Units.FormatTokens(state.TextCharacters)}: ", state.Text, width);
            }
            case "running_tools" when state.ActiveTool != null:
            {
                return WithTail($"running {state.ActiveTool.Tool} {Units.FormatDuration((now - state.ActiveTool.StartedAt).TotalSeconds)}: ", state.ActiveTool.Summary, width);
            }
            case "running_tools" when state.ProposedCalls.Count > 0:
            case "turn_done" when state.ProposedCalls.Count > 0:
            {
                var call = state.ProposedCalls[^1];
                return WithTail($"calling {call.Tool}: ", call.Summary, width);
            }
            case "starting":
            {
                return "starting";
            }
            default:
            {
                return null;
            }
        }
    }

    private static string FormatFile(SessionSummary session, DateTimeOffset now)
    {
        switch (session.DisplayState)
        {
            case SessionStates.Running:
            {
                return session.LastActivity ?? "running";
            }
            case "approval":
            {
                return $"{session.PendingCount} pending ({session.NeedsHumanCount} needs a human)";
            }
            case "finished":
            {
                return $"finished {Units.FormatAge(now - session.UpdatedAt)} ago";
            }
            case "failed":
            {
                return $"failed: {session.ErrorMessage ?? "unknown error"}";
            }
            case "stopped":
            {
                return $"stopped {Units.FormatAge(now - session.UpdatedAt)} ago";
            }
            case "limit":
            {
                return "limit reached";
            }
            case "interrupted":
            {
                return "interrupted";
            }
            default:
            {
                return session.LastActivity ?? session.DisplayState;
            }
        }
    }

    private static string WithTail(string prefix, string tail, int width)
    {
        var collapsed = Collapse(tail);
        var available = Math.Max(0, width - prefix.Length);
        var shown = Tail(collapsed, available);
        return shown.Length == 0 ? prefix.TrimEnd() : prefix + shown;
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Tail(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        return text.Length <= width ? text : text[^width..];
    }
}

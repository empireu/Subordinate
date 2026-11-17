using System.Globalization;
using Subordinate.Tui.Data;
using Subordinate.Tui.Live;

namespace Subordinate.Tui.Presentation;

public static class DetailHeaderFormatter
{
    public static IReadOnlyList<string> Format(SessionSummary session, LiveSessionSnapshot? live, int? contextWindow, DateTimeOffset now)
    {
        var lines = new List<string>();
        var liveText = live?.IsConnected == true ? "  live" : string.Empty;
        lines.Add($"{session.Name}  {session.DisplayState}{liveText}");

        var policy = session.Policy.Count == 0 ? "none" : string.Join(", ", session.Policy);
        lines.Add($"model    {session.ModelAlias} ({session.ModelId})  shell {session.ShellKind}  policy {policy}");

        var run = session.CurrentRun;
        var runText = run == null ? "no run" : $"run {run.RunId} ({run.Kind})";
        var pidText = session.ProcessId.HasValue ? $"  pid {session.ProcessId.Value}" : string.Empty;
        var activityText = session.StatusUpdatedAt.HasValue ? $"  last activity {Units.FormatAge(now - session.StatusUpdatedAt.Value)} ago" : string.Empty;
        lines.Add($"workdir  {session.Workdir}  {runText}{pidText}{activityText}");

        var used = run?.Counters.ModelCalls ?? 0;
        var left = Math.Max(0, session.MaxTurns - used);
        lines.Add($"time     run {Units.FormatDuration(SessionTimes.RunSeconds(session, now))}  session {Units.FormatDuration(SessionTimes.SessionSeconds(session, now))}"
            + $"  turns {used} of {session.MaxTurns} ({left} left)  tools {session.SessionCounters.ToolCalls}  pending {session.PendingCount} ({session.NeedsHumanCount} need a human)");

        var input = session.SessionCounters.InputTokens;
        var cached = session.SessionCounters.CachedInputTokens;
        var uncached = Math.Max(0, input - cached);
        var cachedPercent = input > 0 ? (int)Math.Round(cached * 100.0 / input, MidpointRounding.AwayFromZero) : 0;
        lines.Add($"tokens   in {Units.FormatTokens(input)} (uncached {Units.FormatTokens(uncached)}, cached {Units.FormatTokens(cached)}, {cachedPercent}% cached)  out {Units.FormatTokens(session.SessionCounters.OutputTokens)}");

        var lastTurn = FormatLastTurn(live);

        if (session.ContextTokens <= 0)
        {
            lines.Add($"context  not recorded yet{lastTurn}");
        }
        else if (contextWindow is > 0)
        {
            var fraction = Math.Clamp(session.ContextTokens / (double)contextWindow.Value, 0, 1);
            var filled = Units.ContextBarCells(fraction);
            var bar = new string('#', filled) + new string('.', 20 - filled);
            var percent = Units.ContextPercent(fraction);
            lines.Add($"context  {Units.FormatTokens(session.ContextTokens)} of {Units.FormatTokens(contextWindow.Value)} [{bar}] {percent}%{lastTurn}");
        }
        else
        {
            lines.Add($"context  {Units.FormatTokens(session.ContextTokens)}{lastTurn}");
        }

        return lines;
    }

    private static string FormatLastTurn(LiveSessionSnapshot? live)
    {
        var lastTurn = live?.State.LastTurn;

        if (lastTurn == null)
        {
            return string.Empty;
        }

        var text = $"  last turn {lastTurn.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}s, {Units.FormatTokens(lastTurn.OutputTokens ?? 0)} out";

        if (lastTurn.DurationSeconds >= 0.5 && lastTurn.OutputTokens.HasValue)
        {
            text += $", {Units.TokensPerSecond(lastTurn.OutputTokens.Value, lastTurn.DurationSeconds)} tok/s";
        }

        return text;
    }
}

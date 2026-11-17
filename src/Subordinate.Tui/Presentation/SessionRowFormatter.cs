using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;

namespace Subordinate.Tui.Presentation;

public static class SessionRowFormatter
{
    public const int MarkerWidth = 1;
    public const int StateWidth = 11;
    public const int NameWidth = 20;
    public const int ModelWidth = 14;
    public const int TimeWidth = 7;
    public const int TurnsWidth = 8;
    public const int TokensWidth = 12;
    public const int ContextWidth = 7;

    public static int ActivityWidth(int width)
    {
        var fixedWidth = MarkerWidth + 1 + StateWidth + 1 + NameWidth + 1 + TimeWidth + 1;

        if (width >= 100)
        {
            fixedWidth += ModelWidth + 1 + TokensWidth + 1;
        }

        if (width >= 70)
        {
            fixedWidth += TurnsWidth + 1 + ContextWidth + 1;
        }

        return Math.Max(1, width - fixedWidth);
    }

    public static string FormatHeader(int width)
    {
        var columns = new List<string>
        {
            Pad(string.Empty, MarkerWidth),
            Pad("state", StateWidth),
            Pad("name", NameWidth)
        };

        if (width >= 100)
        {
            columns.Add(Pad("model", ModelWidth));
        }

        columns.Add(Pad("time", TimeWidth));

        if (width >= 70)
        {
            columns.Add(Pad("turns", TurnsWidth));
        }

        if (width >= 100)
        {
            columns.Add(Pad("in/out", TokensWidth));
        }

        if (width >= 70)
        {
            columns.Add(Pad("context", ContextWidth));
        }

        columns.Add(Pad("activity", ActivityWidth(width)));

        return string.Join(" ", columns).TrimEnd();
    }

    public static string FormatRow(SessionSummary session, bool hasLiveConnection, string activity, int spinnerIndex, int width, DateTimeOffset now)
    {
        var columns = new List<string>
        {
            Pad(Marker(session, hasLiveConnection, spinnerIndex), MarkerWidth),
            Pad(Cut(session.DisplayState, StateWidth), StateWidth),
            Pad(Cut(session.Name, NameWidth), NameWidth)
        };

        if (width >= 100)
        {
            columns.Add(Pad(Cut(session.ModelAlias, ModelWidth), ModelWidth));
        }

        columns.Add(Pad(Cut(TimeText(session, now), TimeWidth), TimeWidth));

        if (width >= 70)
        {
            columns.Add(Pad(Cut(TurnsText(session), TurnsWidth), TurnsWidth));
        }

        if (width >= 100)
        {
            columns.Add(Pad(Cut(TokensText(session), TokensWidth), TokensWidth));
        }

        if (width >= 70)
        {
            columns.Add(Pad(Cut(session.ContextTokens > 0 ? Units.FormatTokens(session.ContextTokens) : "-", ContextWidth), ContextWidth));
        }

        columns.Add(Pad(Cut(activity, ActivityWidth(width)), ActivityWidth(width)));

        return string.Join(" ", columns).TrimEnd();
    }

    private static string Marker(SessionSummary session, bool hasLiveConnection, int spinnerIndex)
    {
        if (hasLiveConnection)
        {
            return "|/-\\"[spinnerIndex % 4].ToString();
        }

        if (session.DisplayState == SessionStates.Running)
        {
            return ".";
        }

        if (session.DisplayState == "approval")
        {
            return "!";
        }

        if (session.DisplayState == "failed")
        {
            return "x";
        }

        return " ";
    }

    private static string TimeText(SessionSummary session, DateTimeOffset now)
    {
        if (session.ElapsedSeconds.HasValue || session.CurrentRun != null)
        {
            return Units.FormatDuration(SessionTimes.RunSeconds(session, now));
        }

        return Units.FormatDuration(Math.Max(0, (now - session.UpdatedAt).TotalSeconds));
    }

    private static string TurnsText(SessionSummary session)
    {
        return $"{session.CurrentRun?.Counters.ModelCalls ?? 0}/{session.MaxTurns}";
    }

    private static string TokensText(SessionSummary session)
    {
        return $"{Units.FormatTokens(session.SessionCounters.InputTokens)}/{Units.FormatTokens(session.SessionCounters.OutputTokens)}";
    }

    private static string Cut(string value, int width)
    {
        if (value.Length <= width)
        {
            return value;
        }

        if (width <= 3)
        {
            return value[..width];
        }

        return value[..(width - 3)] + "...";
    }

    private static string Pad(string value, int width)
    {
        return value.PadRight(width);
    }
}

using Subordinate.Tui.Data;

namespace Subordinate.Tui.Presentation;

public static class SessionSelection
{
    public static List<SessionSummary> FilterAndSort(IReadOnlyList<SessionSummary> sessions, bool all)
    {
        var filtered = sessions.Where(session => all || IsVisible(session)).ToList();
        filtered.Sort(Compare);
        return filtered;
    }

    private static bool IsVisible(SessionSummary session)
    {
        return session.DisplayState is "running" or "approval" or "limit" or "interrupted" or "failed";
    }

    private static int Compare(SessionSummary left, SessionSummary right)
    {
        var leftRank = Rank(left);
        var rightRank = Rank(right);

        if (leftRank != rightRank)
        {
            return leftRank.CompareTo(rightRank);
        }

        if (leftRank == 0)
        {
            var leftStart = left.CurrentRun?.StartedAt ?? left.UpdatedAt;
            var rightStart = right.CurrentRun?.StartedAt ?? right.UpdatedAt;
            return rightStart.CompareTo(leftStart);
        }

        return right.UpdatedAt.CompareTo(left.UpdatedAt);
    }

    private static int Rank(SessionSummary session)
    {
        return session.DisplayState switch
        {
            "running" => 0,
            "approval" => 1,
            _ => 2
        };
    }
}

using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;

namespace Subordinate.Tui.Presentation;

public static class SessionTimes
{
    /// <summary>
    ///     The duration of the current run. A run that is not running ends at its recorded end, or at the last record update when it has none.
    /// </summary>
    public static double RunSeconds(SessionSummary session, DateTimeOffset now)
    {
        if (session.ElapsedSeconds.HasValue)
        {
            return session.ElapsedSeconds.Value;
        }

        var run = session.CurrentRun;

        if (run == null)
        {
            return 0;
        }

        var end = run.EndedAt ?? (session.DisplayState == SessionStates.Running ? now : session.UpdatedAt);
        return Math.Max(0, (end - run.StartedAt).TotalSeconds);
    }

    public static double SessionSeconds(SessionSummary session, DateTimeOffset now)
    {
        var end = session.CurrentRun?.EndedAt ?? (session.DisplayState == SessionStates.Running ? now : session.UpdatedAt);
        return Math.Max(0, (end - session.SessionStartedAt).TotalSeconds);
    }
}

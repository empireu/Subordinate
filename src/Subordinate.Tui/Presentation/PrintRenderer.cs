using System.Text;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Tui.Data;
using Subordinate.Tui.Live;

namespace Subordinate.Tui.Presentation;

public static class PrintRenderer
{
    public static string RenderList(string home, string feedStatus, IReadOnlyList<SessionSummary> sessions, Func<SessionSummary, LiveSessionSnapshot?> liveLookup, int width, DateTimeOffset now)
    {
        var builder = new StringBuilder();
        builder.Append($"home: {home}  live feed: {feedStatus}").Append('\n');
        builder.Append(SessionRowFormatter.FormatHeader(width)).Append('\n');

        if (sessions.Count == 0)
        {
            builder.Append($"No sessions in {home}.").Append('\n');
            return builder.ToString();
        }

        var activityWidth = SessionRowFormatter.ActivityWidth(width);

        foreach (var session in sessions)
        {
            var live = liveLookup(session);
            var activity = ActivityHint.Format(session, live, now, activityWidth);
            builder.Append(SessionRowFormatter.FormatRow(session, live?.IsConnected == true, activity, 0, width, now)).Append('\n');
        }

        return builder.ToString();
    }

    public static string RenderDetail(string home, string feedStatus, SessionSummary session, IReadOnlyList<ChatMessage> messages, LiveSessionSnapshot? live, int? contextWindow, int width, DateTimeOffset now)
    {
        var builder = new StringBuilder();
        builder.Append($"home: {home}  live feed: {feedStatus}").Append('\n');

        foreach (var line in DetailHeaderFormatter.Format(session, live, contextWindow, now))
        {
            builder.Append(line).Append('\n');
        }

        var state = new HistoryFormatState();
        var lines = new List<HistoryLine>();

        foreach (var message in messages)
        {
            HistoryLayout.Append(HistoryFormatter.FormatMessage(message, state, true, true), width, true, lines);
        }

        HistoryLayout.Append(HistoryFormatter.FormatLive(live, now), width, true, lines);

        foreach (var line in lines)
        {
            builder.Append(line.Text).Append('\n');
        }

        return builder.ToString();
    }
}

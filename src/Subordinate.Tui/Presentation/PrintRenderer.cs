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

        builder.Append('\n');
        var seenUser = false;

        foreach (var message in messages)
        {
            var isFirstUser = message.Role == ChatRole.User && !seenUser;

            if (message.Role == ChatRole.User)
            {
                seenUser = true;
            }

            foreach (var block in HistoryFormatter.FormatMessage(message, isFirstUser, true, true))
            {
                foreach (var line in TextWrap.Wrap(block.Text, width, 0))
                {
                    builder.Append(line).Append('\n');
                }
            }
        }

        foreach (var block in HistoryFormatter.FormatLive(live, now, width))
        {
            foreach (var line in TextWrap.Wrap(block.Text, width, 0))
            {
                builder.Append(line).Append('\n');
            }
        }

        return builder.ToString();
    }
}

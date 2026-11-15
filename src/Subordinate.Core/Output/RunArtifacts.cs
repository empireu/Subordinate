using System.Globalization;
using System.Text;
using System.Text.Json;
using Subordinate.Core.Json;
using Subordinate.Core.Sessions;

namespace Subordinate.Core.Output;

public sealed class StatusSnapshot
{
    public required string Session { get; init; }

    public required string Workdir { get; init; }

    public required string RunId { get; init; }

    public required string State { get; init; }

    public required int ProcessId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required double ElapsedSeconds { get; init; }

    public required int PendingCount { get; init; }

    public required CountersRecord RunCounters { get; init; }

    public required CountersRecord SessionCounters { get; init; }

    public required int TurnsRemaining { get; init; }

    public required string LastActivity { get; init; }
}

/// <summary>
///     Writes the machine readable and human readable files of a run directory.
/// </summary>
public static class RunArtifacts
{
    public const string StatusFileName = "status.json";
    public const string ResultFileName = "result.json";
    public const string TextFileName = "result.md";
    public const string PendingFileName = "pending.json";
    public const string PendingTextFileName = "pending.md";
    public const string LogFileName = "log.md";

    public static string StatusFile(string runDirectory) => Path.Combine(runDirectory, StatusFileName);

    public static string ResultFile(string runDirectory) => Path.Combine(runDirectory, ResultFileName);

    public static string TextFile(string runDirectory) => Path.Combine(runDirectory, TextFileName);

    public static string PendingFile(string runDirectory) => Path.Combine(runDirectory, PendingFileName);

    public static string PendingTextFile(string runDirectory) => Path.Combine(runDirectory, PendingTextFileName);

    public static string LogFile(string runDirectory) => Path.Combine(runDirectory, LogFileName);

    public static void WriteStatus(string runDirectory, StatusSnapshot status)
    {
        var json = AsciiJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("session", status.Session);
            writer.WriteString("workdir", status.Workdir);
            writer.WriteString("run_id", status.RunId);
            writer.WriteString("state", status.State);
            writer.WriteNumber("pid", status.ProcessId);
            writer.WriteString("started_at", FormatTime(status.StartedAt));
            writer.WriteString("updated_at", FormatTime(DateTimeOffset.UtcNow));
            writer.WriteNumber("elapsed_seconds", Math.Round(status.ElapsedSeconds, 1));
            writer.WriteStartObject("run");
            WriteCounters(writer, status.RunCounters);
            writer.WriteEndObject();
            writer.WriteStartObject("session_totals");
            WriteCounters(writer, status.SessionCounters);
            writer.WriteEndObject();
            writer.WriteNumber("turns_remaining", status.TurnsRemaining);
            writer.WriteNumber("pending_count", status.PendingCount);
            writer.WriteString("last_activity", status.LastActivity);
            writer.WriteEndObject();
        }, true);

        SessionStore.WriteAtomically(StatusFile(runDirectory), json + "\n");
    }

    private static void WriteCounters(Utf8JsonWriter writer, CountersRecord counters)
    {
        writer.WriteNumber("model_calls", counters.ModelCalls);
        writer.WriteNumber("tool_calls", counters.ToolCalls);
        writer.WriteNumber("input_tokens", counters.InputTokens);
        writer.WriteNumber("cached_input_tokens", counters.CachedInputTokens);
        writer.WriteNumber("output_tokens", counters.OutputTokens);
    }

    public static void WriteResult(string runDirectory, SessionRecord record, RunRecord run, string? text)
    {
        var textFile = TextFile(runDirectory);
        SessionStore.WriteAtomically(textFile, text ?? string.Empty);

        var sessionStart = record.Runs.Count > 0 ? record.Runs[0].StartedAt : run.StartedAt;
        var sessionEnd = run.EndedAt ?? DateTimeOffset.UtcNow;

        var json = AsciiJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("session", record.Name);
            writer.WriteString("workdir", record.Workdir);
            writer.WriteString("run_id", run.RunId);
            writer.WriteString("state", run.State);
            writer.WriteBoolean("resumable", IsResumable(run.State));
            writer.WriteString("started_at", FormatTime(run.StartedAt));
            writer.WriteString("ended_at", FormatTime(run.EndedAt ?? DateTimeOffset.UtcNow));
            writer.WriteBoolean("final_answer", record.FinalText != null);
            writer.WriteStartObject("run");
            writer.WriteString("kind", run.Kind);
            writer.WriteNumber("duration_seconds", Math.Round(((run.EndedAt ?? DateTimeOffset.UtcNow) - run.StartedAt).TotalSeconds, 1));
            WriteCounters(writer, run.Counters);
            writer.WriteEndObject();
            writer.WriteStartObject("session_totals");
            writer.WriteString("started_at", FormatTime(sessionStart));
            writer.WriteString("ended_at", FormatTime(sessionEnd));
            writer.WriteNumber("duration_seconds", Math.Round((sessionEnd - sessionStart).TotalSeconds, 1));
            WriteCounters(writer, record.Counters);
            writer.WriteEndObject();
            writer.WriteStartObject("limit");
            writer.WriteNumber("max_turns", record.Limits.MaxTurns);
            writer.WriteNumber("turns_used", run.Counters.ModelCalls);
            writer.WriteBoolean("final_turn", run.FinalTurnForced);
            writer.WriteEndObject();
            writer.WriteNumber("pending_count", record.PendingCount);
            writer.WriteString("pending_file", PendingFile(runDirectory));
            writer.WriteString("text_file", textFile);
            writer.WriteNumber("text_chars", text?.Length ?? 0);
            writer.WriteStringArray("files_written", record.FilesWritten);
            WriteError(writer, record.Error);
            writer.WriteEndObject();
        }, true);

        SessionStore.WriteAtomically(ResultFile(runDirectory), json + "\n");
    }

    public static void WritePending(string runDirectory, SessionRecord record)
    {
        var open = record.Pending.Where(item => item.Status == PendingStatuses.Pending).ToList();

        var json = AsciiJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("session", record.Name);
            writer.WriteString("workdir", record.Workdir);
            writer.WriteNumber("count", open.Count);
            writer.WriteStartArray("items");

            foreach (var item in open)
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("status", item.Status);
                writer.WriteString("tool", item.Tool);
                writer.WriteString("summary", item.Summary);
                writer.WriteNullableString("command", item.Command);
                writer.WriteNullableString("shell", item.Shell);
                writer.WriteNullableString("working_directory", item.WorkingDirectory);
                writer.WriteNullableString("path", item.Path);
                writer.WriteStringArray("capabilities", item.Capabilities);
                writer.WriteStringArray("missing", item.Missing);
                writer.WriteStringArray("reasons", item.Reasons);
                writer.WriteString("proposed_scope", item.ProposedScope);
                writer.WriteBoolean("needs_human", item.NeedsHuman);
                writer.WriteString("call_id", item.CallId);
                writer.WriteString("run_id", item.RunId);
                writer.WriteString("created_at", FormatTime(item.CreatedAt));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }, true);

        SessionStore.WriteAtomically(PendingFile(runDirectory), json + "\n");
        SessionStore.WriteAtomically(PendingTextFile(runDirectory), RenderPending(record, open));
    }

    public static string RenderPending(SessionRecord record, IReadOnlyList<PendingRecord> open)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"# Pending approvals for \"{record.Name}\" ({open.Count})\n\n");

        if (open.Count == 0)
        {
            builder.Append("Nothing is waiting for approval.\n");
            return builder.ToString();
        }

        foreach (var item in open)
        {
            builder.Append(CultureInfo.InvariantCulture, $"## {item.Id}{(item.NeedsHuman ? " (needs a human)" : string.Empty)}\n\n");
            builder.Append(CultureInfo.InvariantCulture, $"- tool: {item.Tool}{(item.Shell != null ? $" ({item.Shell})" : string.Empty)}\n");

            if (item.Command != null)
            {
                builder.Append(CultureInfo.InvariantCulture, $"- command: `{item.Command}`\n");
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"- action: {item.Summary}\n");
            }

            if (item.WorkingDirectory != null)
            {
                builder.Append(CultureInfo.InvariantCulture, $"- working directory: {item.WorkingDirectory}\n");
            }

            builder.Append(CultureInfo.InvariantCulture, $"- missing: {string.Join(", ", item.Missing)}\n");
            builder.Append(CultureInfo.InvariantCulture, $"- proposed scope: {item.ProposedScope}\n");

            foreach (var reason in item.Reasons)
            {
                builder.Append(CultureInfo.InvariantCulture, $"- why: {reason}\n");
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    public static void AppendLog(string runDirectory, string line)
    {
        try
        {
            File.AppendAllText(LogFile(runDirectory), line + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static bool IsResumable(string state)
    {
        return state is SessionStates.AwaitingApproval or SessionStates.LimitReached or SessionStates.Stopped or SessionStates.Failed or SessionStates.Interrupted or SessionStates.Finished;
    }

    public static void WriteError(Utf8JsonWriter writer, ErrorRecord? error)
    {
        if (error == null)
        {
            writer.WriteNull("error");
            return;
        }

        writer.WriteStartObject("error");
        writer.WriteString("code", error.Code);
        writer.WriteString("message", error.Message);
        writer.WriteEndObject();
    }

    public static string FormatTime(DateTimeOffset time)
    {
        return time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}

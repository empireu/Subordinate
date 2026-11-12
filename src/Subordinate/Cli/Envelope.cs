using System.Text.Json;
using Subordinate.Core;
using Subordinate.Core.Json;
using Subordinate.Core.Output;
using Subordinate.Core.Sessions;

namespace Subordinate.Cli;

/// <summary>
///     The single JSON line every command prints first on stdout. Keys always appear in the same order, and the text is ASCII only.
/// </summary>
public sealed class Envelope
{
    public required string Command { get; init; }

    public bool Ok { get; init; } = true;

    public string? Session { get; init; }

    public string? Workdir { get; init; }

    public string? State { get; init; }

    public string? RunId { get; init; }

    public string? RunDirectory { get; init; }

    public int? PendingCount { get; init; }

    public bool? Resumable { get; init; }

    public ErrorCode? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Command specific properties, written after the common ones.
    /// </summary>
    public Action<Utf8JsonWriter>? Extra { get; init; }

    public static Envelope ForSession(string command, SessionRecord record, string? runDirectory, Action<Utf8JsonWriter>? extra = null, string? stateOverride = null)
    {
        var state = stateOverride ?? record.State;

        return new Envelope
        {
            Command = command,
            Session = record.Name,
            Workdir = record.Workdir,
            State = state,
            RunId = record.CurrentRun?.RunId,
            RunDirectory = runDirectory,
            PendingCount = record.PendingCount,
            Resumable = RunArtifacts.IsResumable(state),
            Extra = extra
        };
    }

    public static Envelope ForError(string command, SubordinateException exception)
    {
        return new Envelope
        {
            Command = command,
            Ok = false,
            ErrorCode = exception.Code,
            ErrorMessage = exception.Message
        };
    }

    public string ToJson()
    {
        return AsciiJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", Ok);
            writer.WriteString("command", Command);
            writer.WriteNullableString("session", Session);
            writer.WriteNullableString("workdir", Workdir);
            writer.WriteNullableString("state", State);
            writer.WriteNullableString("run_id", RunId);
            writer.WriteNullableString("run_dir", RunDirectory);
            writer.WriteNullableString("result_file", RunDirectory == null ? null : RunArtifacts.ResultFile(RunDirectory));
            writer.WriteNullableString("text_file", RunDirectory == null ? null : RunArtifacts.TextFile(RunDirectory));
            writer.WriteNullableString("pending_file", RunDirectory == null ? null : RunArtifacts.PendingFile(RunDirectory));

            if (PendingCount.HasValue)
            {
                writer.WriteNumber("pending_count", PendingCount.Value);
            }
            else
            {
                writer.WriteNull("pending_count");
            }

            if (Resumable.HasValue)
            {
                writer.WriteBoolean("resumable", Resumable.Value);
            }
            else
            {
                writer.WriteNull("resumable");
            }

            if (ErrorCode.HasValue)
            {
                writer.WriteStartObject("error");
                writer.WriteString("code", ErrorCode.Value.ToToken());
                writer.WriteNumber("exit_code", ErrorCode.Value.ToExitCode());
                writer.WriteString("message", ErrorMessage ?? string.Empty);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("error");
            }

            Extra?.Invoke(writer);
            writer.WriteEndObject();
        }, false);
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Subordinate.Core.Json;
using Subordinate.Core.Output;

namespace Subordinate.Core.Monitoring;

public enum MonitorParseOutcome
{
    Ok,
    Ignored,
    Invalid
}

public sealed record MonitorParseResult(MonitorParseOutcome Outcome, MonitorMessage? Message)
{
    public static MonitorParseResult Ok(MonitorMessage message)
    {
        return new MonitorParseResult(MonitorParseOutcome.Ok, message);
    }

    public static readonly MonitorParseResult Ignored = new(MonitorParseOutcome.Ignored, null);

    public static readonly MonitorParseResult Invalid = new(MonitorParseOutcome.Invalid, null);
}

public static class MonitorProtocol
{
    public const int Version = 1;
    public const int MaxLineBytes = 1_048_576;
    public const int MaxDeltaCharacters = 16_384;
    public const int LiveTextTailCharacters = 32_768;
    public const int MaxProposedCalls = 64;
    public const int MaxSummaryCharacters = 300;
    public const int MaxFirstLineCharacters = 300;
    public const int MaxMessageCharacters = 2000;

    public static string Serialize(MonitorMessage message)
    {
        return AsciiJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);

            switch (message)
            {
                case MonitorHello hello:
                {
                    writer.WriteString("type", "hello");
                    writer.WriteString("session_dir", hello.SessionDirectory);
                    writer.WriteString("session", hello.Session);
                    writer.WriteString("workdir", hello.Workdir);
                    writer.WriteString("run_id", hello.RunId);
                    writer.WriteString("run_kind", hello.RunKind);
                    writer.WriteNumber("pid", hello.ProcessId);
                    writer.WriteString("model_alias", hello.ModelAlias);
                    writer.WriteString("model_id", hello.ModelId);
                    writer.WriteString("started_at", RunArtifacts.FormatTime(hello.StartedAt));
                    break;
                }
                case MonitorSnapshot snapshot:
                {
                    writer.WriteString("type", "snapshot");
                    writer.WriteString("phase", snapshot.Phase);
                    writer.WriteString("phase_started_at", RunArtifacts.FormatTime(snapshot.PhaseStartedAt));
                    writer.WriteNumber("model_call", snapshot.ModelCall);
                    writer.WriteString("reasoning", snapshot.Reasoning);
                    writer.WriteNumber("reasoning_chars", snapshot.ReasoningCharacters);
                    writer.WriteString("text", snapshot.Text);
                    writer.WriteNumber("text_chars", snapshot.TextCharacters);
                    writer.WriteStartArray("proposed_calls");

                    foreach (var call in snapshot.ProposedCalls)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("call_id", call.CallId);
                        writer.WriteString("tool", call.Tool);
                        writer.WriteString("summary", call.Summary);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();

                    if (snapshot.ActiveTool == null)
                    {
                        writer.WriteNull("active_tool");
                    }
                    else
                    {
                        writer.WriteStartObject("active_tool");
                        writer.WriteString("call_id", snapshot.ActiveTool.CallId);
                        writer.WriteString("tool", snapshot.ActiveTool.Tool);
                        writer.WriteString("summary", snapshot.ActiveTool.Summary);
                        writer.WriteString("started_at", RunArtifacts.FormatTime(snapshot.ActiveTool.StartedAt));
                        writer.WriteEndObject();
                    }

                    if (snapshot.Status == null)
                    {
                        writer.WriteNull("status");
                    }
                    else
                    {
                        WriteStatusPayload(writer, "status", snapshot.Status);
                    }

                    writer.WriteNumber("transcript_count", snapshot.TranscriptCount);

                    if (snapshot.LastTurn == null)
                    {
                        writer.WriteNull("last_turn");
                    }
                    else
                    {
                        writer.WriteStartObject("last_turn");
                        writer.WriteNumber("duration_seconds", snapshot.LastTurn.DurationSeconds);
                        WriteNullableNumber(writer, "output_tokens", snapshot.LastTurn.OutputTokens);
                        writer.WriteEndObject();
                    }

                    break;
                }
                case MonitorTurnStarted turnStarted:
                {
                    writer.WriteString("type", "turn_started");
                    writer.WriteNumber("model_call", turnStarted.ModelCall);
                    writer.WriteString("at", RunArtifacts.FormatTime(turnStarted.At));
                    break;
                }
                case MonitorReasoningDelta reasoningDelta:
                {
                    writer.WriteString("type", "reasoning_delta");
                    writer.WriteString("text", reasoningDelta.Text);
                    break;
                }
                case MonitorTextDelta textDelta:
                {
                    writer.WriteString("type", "text_delta");
                    writer.WriteString("text", textDelta.Text);
                    break;
                }
                case MonitorToolCallProposed toolCallProposed:
                {
                    writer.WriteString("type", "tool_call_proposed");
                    writer.WriteString("call_id", toolCallProposed.CallId);
                    writer.WriteString("tool", toolCallProposed.Tool);
                    writer.WriteString("summary", toolCallProposed.Summary);
                    break;
                }
                case MonitorTurnCompleted turnCompleted:
                {
                    writer.WriteString("type", "turn_completed");
                    writer.WriteString("finish_reason", turnCompleted.FinishReason);
                    WriteNullableNumber(writer, "input_tokens", turnCompleted.InputTokens);
                    WriteNullableNumber(writer, "cached_input_tokens", turnCompleted.CachedInputTokens);
                    WriteNullableNumber(writer, "output_tokens", turnCompleted.OutputTokens);
                    writer.WriteString("at", RunArtifacts.FormatTime(turnCompleted.At));
                    break;
                }
                case MonitorTurnFailed turnFailed:
                {
                    writer.WriteString("type", "turn_failed");
                    writer.WriteString("message", turnFailed.Message);
                    break;
                }
                case MonitorToolStarted toolStarted:
                {
                    writer.WriteString("type", "tool_started");
                    writer.WriteString("call_id", toolStarted.CallId);
                    writer.WriteString("tool", toolStarted.Tool);
                    writer.WriteString("summary", toolStarted.Summary);
                    writer.WriteString("at", RunArtifacts.FormatTime(toolStarted.At));
                    break;
                }
                case MonitorToolFinished toolFinished:
                {
                    writer.WriteString("type", "tool_finished");
                    writer.WriteString("call_id", toolFinished.CallId);
                    writer.WriteBoolean("ok", toolFinished.Ok);
                    writer.WriteString("first_line", toolFinished.FirstLine);
                    writer.WriteString("at", RunArtifacts.FormatTime(toolFinished.At));
                    break;
                }
                case MonitorStatus status:
                {
                    writer.WriteString("type", "status");
                    WriteStatusPayloadFields(writer, new MonitorStatusPayload(
                        status.State,
                        status.ElapsedSeconds,
                        status.TurnsRemaining,
                        status.PendingCount,
                        status.LastActivity,
                        status.Run,
                        status.SessionTotals,
                        status.ContextTokens));
                    break;
                }
                case MonitorTranscriptCommitted transcriptCommitted:
                {
                    writer.WriteString("type", "transcript_committed");
                    writer.WriteNumber("count", transcriptCommitted.Count);
                    writer.WriteNumber("bytes", transcriptCommitted.Bytes);
                    break;
                }
                case MonitorRunEnded runEnded:
                {
                    writer.WriteString("type", "run_ended");
                    writer.WriteString("state", runEnded.State);
                    writer.WriteNullableString("error_message", runEnded.ErrorMessage);
                    break;
                }
                default:
                {
                    throw new ArgumentException($"Unknown monitor message type {message.GetType().Name}.", nameof(message));
                }
            }

            writer.WriteEndObject();
        }, false);
    }

    public static MonitorParseResult Parse(ReadOnlySpan<byte> line)
    {
        JsonDocument document;

        try
        {
            var reader = new Utf8JsonReader(line);
            document = JsonDocument.ParseValue(ref reader);

            if (reader.Read())
            {
                document.Dispose();
                return MonitorParseResult.Invalid;
            }
        }
        catch (JsonException)
        {
            return MonitorParseResult.Invalid;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return MonitorParseResult.Invalid;
            }

            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != Version)
            {
                return MonitorParseResult.Invalid;
            }

            if (!root.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
            {
                return MonitorParseResult.Invalid;
            }

            try
            {
                var message = ParseMessage(typeProperty.GetString()!, root);
                return message == null ? MonitorParseResult.Ignored : MonitorParseResult.Ok(message);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return MonitorParseResult.Invalid;
            }
        }
    }

    private static MonitorMessage? ParseMessage(string type, JsonElement root)
    {
        switch (type)
        {
            case "hello":
            {
                return new MonitorHello(
                    RequiredString(root, "session_dir"),
                    RequiredString(root, "session"),
                    RequiredString(root, "workdir"),
                    RequiredString(root, "run_id"),
                    RequiredString(root, "run_kind"),
                    RequiredInt32(root, "pid"),
                    RequiredString(root, "model_alias"),
                    RequiredString(root, "model_id"),
                    RequiredTime(root, "started_at"));
            }
            case "snapshot":
            {
                return new MonitorSnapshot(
                    RequiredString(root, "phase"),
                    RequiredTime(root, "phase_started_at"),
                    RequiredInt32(root, "model_call"),
                    RequiredString(root, "reasoning"),
                    RequiredInt64(root, "reasoning_chars"),
                    RequiredString(root, "text"),
                    RequiredInt64(root, "text_chars"),
                    ParseProposedCalls(root),
                    ParseOptionalActiveTool(root),
                    ParseOptionalStatus(root),
                    RequiredInt32(root, "transcript_count"),
                    ParseOptionalLastTurn(root));
            }
            case "turn_started":
            {
                return new MonitorTurnStarted(RequiredInt32(root, "model_call"), RequiredTime(root, "at"));
            }
            case "reasoning_delta":
            {
                return new MonitorReasoningDelta(RequiredString(root, "text"));
            }
            case "text_delta":
            {
                return new MonitorTextDelta(RequiredString(root, "text"));
            }
            case "tool_call_proposed":
            {
                return new MonitorToolCallProposed(RequiredString(root, "call_id"), RequiredString(root, "tool"), RequiredString(root, "summary"));
            }
            case "turn_completed":
            {
                return new MonitorTurnCompleted(
                    RequiredString(root, "finish_reason"),
                    OptionalInt64(root, "input_tokens"),
                    OptionalInt64(root, "cached_input_tokens"),
                    OptionalInt64(root, "output_tokens"),
                    RequiredTime(root, "at"));
            }
            case "turn_failed":
            {
                return new MonitorTurnFailed(RequiredString(root, "message"));
            }
            case "tool_started":
            {
                return new MonitorToolStarted(
                    RequiredString(root, "call_id"),
                    RequiredString(root, "tool"),
                    RequiredString(root, "summary"),
                    RequiredTime(root, "at"));
            }
            case "tool_finished":
            {
                return new MonitorToolFinished(
                    RequiredString(root, "call_id"),
                    RequiredBoolean(root, "ok"),
                    RequiredString(root, "first_line"),
                    RequiredTime(root, "at"));
            }
            case "status":
            {
                var payload = ParseStatusPayload(root);
                return new MonitorStatus(
                    payload.State,
                    payload.ElapsedSeconds,
                    payload.TurnsRemaining,
                    payload.PendingCount,
                    payload.LastActivity,
                    payload.Run,
                    payload.SessionTotals,
                    payload.ContextTokens);
            }
            case "transcript_committed":
            {
                return new MonitorTranscriptCommitted(RequiredInt32(root, "count"), RequiredInt64(root, "bytes"));
            }
            case "run_ended":
            {
                return new MonitorRunEnded(RequiredString(root, "state"), OptionalString(root, "error_message"));
            }
            default:
            {
                return null;
            }
        }
    }

    private static IReadOnlyList<MonitorProposedCall> ParseProposedCalls(JsonElement root)
    {
        var array = RequiredProperty(root, "proposed_calls");

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException();
        }

        var calls = new List<MonitorProposedCall>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException();
            }

            calls.Add(new MonitorProposedCall(RequiredString(element, "call_id"), RequiredString(element, "tool"), RequiredString(element, "summary")));
        }

        return calls;
    }

    private static MonitorActiveTool? ParseOptionalActiveTool(JsonElement root)
    {
        var element = OptionalProperty(root, "active_tool");

        if (element == null)
        {
            return null;
        }

        if (element.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException();
        }

        return new MonitorActiveTool(
            RequiredString(element.Value, "call_id"),
            RequiredString(element.Value, "tool"),
            RequiredString(element.Value, "summary"),
            RequiredTime(element.Value, "started_at"));
    }

    private static MonitorStatusPayload? ParseOptionalStatus(JsonElement root)
    {
        var element = OptionalProperty(root, "status");

        if (element == null)
        {
            return null;
        }

        if (element.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException();
        }

        return ParseStatusPayload(element.Value);
    }

    private static MonitorLastTurn? ParseOptionalLastTurn(JsonElement root)
    {
        var element = OptionalProperty(root, "last_turn");

        if (element == null)
        {
            return null;
        }

        if (element.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException();
        }

        return new MonitorLastTurn(RequiredDouble(element.Value, "duration_seconds"), OptionalInt64(element.Value, "output_tokens"));
    }

    private static MonitorStatusPayload ParseStatusPayload(JsonElement element)
    {
        return new MonitorStatusPayload(
            RequiredString(element, "state"),
            RequiredDouble(element, "elapsed_seconds"),
            RequiredInt32(element, "turns_remaining"),
            RequiredInt32(element, "pending_count"),
            RequiredString(element, "last_activity"),
            ParseCounters(RequiredProperty(element, "run")),
            ParseCounters(RequiredProperty(element, "session_totals")),
            RequiredInt64(element, "context_tokens"));
    }

    private static MonitorStatusCounters ParseCounters(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException();
        }

        return new MonitorStatusCounters(
            RequiredInt32(element, "model_calls"),
            RequiredInt32(element, "tool_calls"),
            RequiredInt64(element, "input_tokens"),
            RequiredInt64(element, "cached_input_tokens"),
            RequiredInt64(element, "output_tokens"));
    }

    private static void WriteStatusPayload(Utf8JsonWriter writer, string propertyName, MonitorStatusPayload status)
    {
        writer.WriteStartObject(propertyName);
        WriteStatusPayloadFields(writer, status);
        writer.WriteEndObject();
    }

    private static void WriteStatusPayloadFields(Utf8JsonWriter writer, MonitorStatusPayload status)
    {
        writer.WriteString("state", status.State);
        writer.WriteNumber("elapsed_seconds", status.ElapsedSeconds);
        writer.WriteNumber("turns_remaining", status.TurnsRemaining);
        writer.WriteNumber("pending_count", status.PendingCount);
        writer.WriteString("last_activity", status.LastActivity);
        WriteCounters(writer, "run", status.Run);
        WriteCounters(writer, "session_totals", status.SessionTotals);
        writer.WriteNumber("context_tokens", status.ContextTokens);
    }

    private static void WriteCounters(Utf8JsonWriter writer, string propertyName, MonitorStatusCounters counters)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteNumber("model_calls", counters.ModelCalls);
        writer.WriteNumber("tool_calls", counters.ToolCalls);
        writer.WriteNumber("input_tokens", counters.InputTokens);
        writer.WriteNumber("cached_input_tokens", counters.CachedInputTokens);
        writer.WriteNumber("output_tokens", counters.OutputTokens);
        writer.WriteEndObject();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static JsonElement RequiredProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException();
        }

        return value;
    }

    private static JsonElement? OptionalProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException();
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        var value = OptionalProperty(element, name);

        if (value == null)
        {
            return null;
        }

        if (value.Value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException();
        }

        return value.Value.GetString();
    }

    private static int RequiredInt32(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new InvalidDataException();
        }

        return number;
    }

    private static long RequiredInt64(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw new InvalidDataException();
        }

        return number;
    }

    private static long? OptionalInt64(JsonElement element, string name)
    {
        var value = OptionalProperty(element, name);

        if (value == null)
        {
            return null;
        }

        if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetInt64(out var number))
        {
            throw new InvalidDataException();
        }

        return number;
    }

    private static double RequiredDouble(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            throw new InvalidDataException();
        }

        return number;
    }

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException();
        }

        return value.GetBoolean();
    }

    private static DateTimeOffset RequiredTime(JsonElement element, string name)
    {
        var text = RequiredString(element, name);

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
        {
            throw new InvalidDataException();
        }

        return time;
    }
}

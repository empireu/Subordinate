using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace Subordinate.Core.Sessions;

/// <summary>
///     Author names of the messages that compaction injects, so the runner and the TUI can find them.
/// </summary>
public static class CompactionAuthors
{
    public const string Notice = "subordinate-compaction-notice";

    public const string Request = "subordinate-compaction-request";

    public const string Summary = "subordinate-compaction-summary";
}

/// <summary>
///     The persisted record of a finished compaction.
/// </summary>
public sealed class CompactionMarker
{
    public DateTimeOffset At { get; set; }

    public int RemovedMessages { get; set; }

    public long TokensBefore { get; set; }

    public string Message { get; set; } = string.Empty;

    public int KeptFromLine { get; set; }

    public int KeptToLine { get; set; }
}

/// <summary>
///     One line of transcript.jsonl: either a chat message or a compaction marker.
/// </summary>
public abstract record TranscriptEntry;

public sealed record TranscriptMessageEntry(ChatMessage Message) : TranscriptEntry;

public sealed record TranscriptCompactionEntry(CompactionMarker Marker) : TranscriptEntry;

/// <summary>
///     Converts chat messages to and from one JSON line each. Unknown shapes fail loudly instead of being dropped.
/// </summary>
public static class TranscriptSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    public static string Serialize(ChatMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteMessage(writer, message);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string SerializeCompaction(CompactionMarker marker)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "compaction");
            writer.WriteString("at", marker.At.ToString("o", CultureInfo.InvariantCulture));
            writer.WriteNumber("removed_messages", marker.RemovedMessages);
            writer.WriteNumber("tokens_before", marker.TokensBefore);
            writer.WriteString("message", marker.Message);
            writer.WriteNumber("kept_from_line", marker.KeptFromLine);
            writer.WriteNumber("kept_to_line", marker.KeptToLine);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", RoleToken(message.Role));

        if (message.AuthorName != null)
        {
            writer.WriteString("author_name", message.AuthorName);
        }

        if (message.MessageId != null)
        {
            writer.WriteString("message_id", message.MessageId);
        }

        if (message.ResponseId != null)
        {
            writer.WriteString("response_id", message.ResponseId);
        }

        if (message.ModelId != null)
        {
            writer.WriteString("model_id", message.ModelId);
        }

        writer.WriteStartArray("contents");

        foreach (var content in message.Contents)
        {
            writer.WriteStartObject();

            switch (content)
            {
                case TextContent text:
                {
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    break;
                }
                case ReasoningContent reasoning:
                {
                    writer.WriteString("type", "reasoning");

                    if (reasoning.Text != null)
                    {
                        writer.WriteString("text", reasoning.Text);
                    }

                    if (reasoning.Signature != null)
                    {
                        writer.WriteString("signature", reasoning.Signature);
                    }

                    writer.WriteBoolean("is_redacted", reasoning.IsRedacted);
                    break;
                }
                case ImageContent image:
                {
                    writer.WriteString("type", "image");
                    writer.WriteString("url", image.Url);

                    if (image.Detail != null)
                    {
                        writer.WriteString("detail", image.Detail);
                    }

                    break;
                }
                case ToolCallContent toolCall:
                {
                    writer.WriteString("type", "tool_call");
                    writer.WriteString("call_id", toolCall.CallId);
                    writer.WriteString("tool_name", toolCall.ToolName);
                    writer.WriteString("arguments", toolCall.Arguments.ValueKind == JsonValueKind.Undefined ? "{}" : toolCall.Arguments.GetRawText());
                    break;
                }
                case ToolResultContent toolResult:
                {
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("call_id", toolResult.CallId);
                    writer.WriteString("tool_name", toolResult.ToolName);
                    writer.WriteString("output", toolResult.Output);
                    writer.WriteBoolean("is_error", toolResult.IsError);
                    break;
                }
                default:
                {
                    throw new InvalidDataException($"Cannot persist message content of type {content.GetType().Name}.");
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static ChatMessage Deserialize(string line)
    {
        using var document = JsonDocument.Parse(line);
        return ReadMessage(document.RootElement);
    }

    public static TranscriptEntry DeserializeEntry(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (root.TryGetProperty("kind", out var kindElement))
        {
            var kind = kindElement.GetString();

            if (kind == "compaction")
            {
                return new TranscriptCompactionEntry(ReadCompaction(root));
            }

            throw new InvalidDataException($"Unknown transcript entry kind \"{kind}\".");
        }

        return new TranscriptMessageEntry(ReadMessage(root));
    }

    private static ChatMessage ReadMessage(JsonElement root)
    {
        var contents = new List<ChatContent>();

        foreach (var content in root.GetProperty("contents").EnumerateArray())
        {
            var type = content.GetProperty("type").GetString();

            contents.Add(type switch
            {
                "text" => new TextContent { Text = content.GetProperty("text").GetString() ?? string.Empty },
                "reasoning" => new ReasoningContent
                {
                    Text = OptionalString(content, "text"),
                    Signature = OptionalString(content, "signature"),
                    IsRedacted = content.TryGetProperty("is_redacted", out var redacted) && redacted.GetBoolean()
                },
                "image" => new ImageContent
                {
                    Url = content.GetProperty("url").GetString() ?? string.Empty,
                    Detail = OptionalString(content, "detail")
                },
                "tool_call" => new ToolCallContent
                {
                    CallId = content.GetProperty("call_id").GetString() ?? string.Empty,
                    ToolName = content.GetProperty("tool_name").GetString() ?? string.Empty,
                    Arguments = ParseArguments(content.GetProperty("arguments").GetString())
                },
                "tool_result" => new ToolResultContent
                {
                    CallId = content.GetProperty("call_id").GetString() ?? string.Empty,
                    ToolName = content.GetProperty("tool_name").GetString() ?? string.Empty,
                    Output = content.GetProperty("output").GetString() ?? string.Empty,
                    IsError = content.TryGetProperty("is_error", out var isError) && isError.GetBoolean()
                },
                _ => throw new InvalidDataException($"Unknown transcript content type \"{type}\".")
            });
        }

        return new ChatMessage
        {
            Role = ParseRole(root.GetProperty("role").GetString()),
            Contents = contents,
            AuthorName = OptionalString(root, "author_name"),
            MessageId = OptionalString(root, "message_id"),
            ResponseId = OptionalString(root, "response_id"),
            ModelId = OptionalString(root, "model_id")
        };
    }

    private static CompactionMarker ReadCompaction(JsonElement root)
    {
        return new CompactionMarker
        {
            At = DateTimeOffset.Parse(root.GetProperty("at").GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            RemovedMessages = root.GetProperty("removed_messages").GetInt32(),
            TokensBefore = root.GetProperty("tokens_before").GetInt64(),
            Message = root.GetProperty("message").GetString() ?? string.Empty,
            KeptFromLine = root.GetProperty("kept_from_line").GetInt32(),
            KeptToLine = root.GetProperty("kept_to_line").GetInt32()
        };
    }

    public static JsonElement ParseArguments(string? json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.Clone();
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string RoleToken(ChatRole role)
    {
        return role switch
        {
            ChatRole.System => "system",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => throw new InvalidDataException($"Cannot persist chat role {role}.")
        };
    }

    private static ChatRole ParseRole(string? role)
    {
        return role switch
        {
            "system" => ChatRole.System,
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => throw new InvalidDataException($"Unknown transcript role \"{role}\".")
        };
    }
}

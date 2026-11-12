using System.Text.Json;

namespace KnowledgeSystem.Ai.Providers.Abstractions;

public abstract class ChatContent
{
}

public sealed class TextContent : ChatContent
{
    public required string Text { get; init; }
}

public sealed class ReasoningContent : ChatContent
{
    public string? Text { get; init; }
    public string? Signature { get; init; }
    public bool IsRedacted { get; init; }
}

public sealed class ImageContent : ChatContent
{
    public required string Url { get; init; }
    public string? Detail { get; init; }

    public static ImageContent FromBytes(ReadOnlySpan<byte> data, string mediaType)
    {
        var base64 = Convert.ToBase64String(data);

        return new ImageContent
        {
            Url = $"data:{mediaType};base64,{base64}"
        };
    }
}

public sealed class ToolCallContent : ChatContent
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required JsonElement Arguments { get; init; }
}

public sealed class ToolResultContent : ChatContent
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required string Output { get; init; }
    public bool IsError { get; init; }
}

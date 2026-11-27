using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KnowledgeSystem.Ai.Providers.Protocols.Mistral;

internal sealed class MistralChatRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required List<MistralMessage> Messages { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MistralTool>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; init; }

    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("parallel_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParallelToolCalls { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MistralStreamOptions? StreamOptions { get; init; }
}

internal sealed class MistralStreamOptions
{
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; init; }
}

internal sealed class MistralMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MistralToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

internal sealed class MistralToolCall
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required MistralFunctionCall Function { get; init; }

    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; init; }
}

internal sealed class MistralFunctionCall
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }
}

internal sealed class MistralTool
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required MistralFunctionDefinition Function { get; init; }
}

internal sealed class MistralFunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    public required JsonElement Parameters { get; init; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; init; }
}

internal sealed class MistralCompletionChunk
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("usage")]
    public MistralUsage? Usage { get; init; }

    [JsonPropertyName("choices")]
    public List<MistralStreamChoice>? Choices { get; init; }
}

internal sealed class MistralStreamChoice
{
    [JsonPropertyName("delta")]
    public MistralDelta? Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

internal sealed class MistralDelta
{
    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    public List<MistralToolCall>? ToolCalls { get; init; }
}

internal sealed class MistralUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; init; }

    [JsonPropertyName("prompt_tokens_details")]
    public MistralPromptTokensDetails? PromptTokensDetails { get; init; }

    [JsonPropertyName("completion_tokens_details")]
    public MistralCompletionTokensDetails? CompletionTokensDetails { get; init; }
}

internal sealed class MistralPromptTokensDetails
{
    [JsonPropertyName("cached_tokens")]
    public int? CachedTokens { get; init; }
}

internal sealed class MistralCompletionTokensDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public int? ReasoningTokens { get; init; }
}

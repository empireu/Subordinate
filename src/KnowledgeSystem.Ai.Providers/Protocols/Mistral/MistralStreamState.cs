using System.Text;
using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace KnowledgeSystem.Ai.Providers.Protocols.Mistral;

internal sealed class MistralStreamState
{
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly List<ContentBuilder> _contents = [];
    private readonly Dictionary<int, int> _toolCallContentIndices = [];
    private ChatFinishReason _finishReason = ChatFinishReason.Unknown;
    private bool _isFinalized;

    public ChatFinishReason FinishReason => _finishReason;

    public bool IsCompleted => _finishReason != ChatFinishReason.Unknown;

    public IEnumerable<ChatStreamEvent> Process(MistralCompletionChunk chunk)
    {
        var events = new List<ChatStreamEvent>();

        if (chunk.Choices == null)
        {
            return events;
        }

        foreach (var choice in chunk.Choices)
        {
            if (choice.Delta != null)
            {
                ProcessDelta(choice.Delta, events);
            }

            if (choice.FinishReason != null)
            {
                _finishReason = MapFinishReason(choice.FinishReason);
                Finalize(events);
            }
        }

        return events;
    }

    public void FinalizeRemaining(List<ChatStreamEvent> events)
    {
        Finalize(events);
    }

    public ChatMessage BuildMessage(string? responseId, string? modelId)
    {
        var contents = new List<ChatContent>(_contents.Count);

        foreach (var content in _contents)
        {
            contents.Add(content.Build());
        }

        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Contents = contents,
            ResponseId = responseId,
            ModelId = modelId
        };
    }

    public static ChatUsage MapUsage(MistralUsage usage)
    {
        return new ChatUsage
        {
            InputTokens = usage.PromptTokens,
            OutputTokens = usage.CompletionTokens,
            CachedInputTokens = usage.PromptTokensDetails?.CachedTokens,
            ReasoningTokens = usage.CompletionTokensDetails?.ReasoningTokens,
            TotalTokens = usage.TotalTokens
        };
    }

    private void ProcessDelta(MistralDelta delta, List<ChatStreamEvent> events)
    {
        if (delta.Content.HasValue)
        {
            ProcessContent(delta.Content.Value, events);
        }

        if (delta.ToolCalls != null)
        {
            foreach (var toolCall in delta.ToolCalls)
            {
                ProcessToolCall(toolCall);
            }
        }
    }

    private void ProcessContent(JsonElement content, List<ChatStreamEvent> events)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            AppendText(content.GetString() ?? string.Empty, events);
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var chunk in content.EnumerateArray())
        {
            var type = chunk.TryGetProperty("type", out var typeProperty)
                ? typeProperty.GetString()
                : null;

            switch (type)
            {
                case "text":
                {
                    var text = chunk.TryGetProperty("text", out var textProperty)
                        ? textProperty.GetString()
                        : null;

                    AppendText(text ?? string.Empty, events);
                    break;
                }
                case "thinking":
                {
                    var text = ExtractThinkingText(chunk);
                    var signature = chunk.TryGetProperty("signature", out var signatureProperty)
                        ? signatureProperty.GetString()
                        : null;

                    AppendReasoning(text, signature, events);
                    break;
                }
                case "image_url":
                {
                    var image = MapImage(chunk);

                    if (image != null)
                    {
                        _contents.Add(new ImageBuilder(image));
                    }

                    break;
                }
            }
        }
    }

    private void AppendText(string text, List<ChatStreamEvent> events)
    {
        if (text.Length == 0)
        {
            return;
        }

        var index = FindLastIndex<TextBuilder>();

        if (index < 0)
        {
            var builder = new TextBuilder();
            _contents.Add(builder);
            index = _contents.Count - 1;
            events.Add(new TextStartedEvent(index));
        }

        var textBuilder = (TextBuilder)_contents[index];
        textBuilder.Text.Append(text);
        events.Add(new TextDeltaEvent(index, text));
    }

    private void AppendReasoning(string text, string? signature, List<ChatStreamEvent> events)
    {
        if (text.Length == 0 && string.IsNullOrEmpty(signature))
        {
            return;
        }

        var index = FindLastIndex<ReasoningBuilder>();

        if (index < 0)
        {
            var builder = new ReasoningBuilder();
            _contents.Add(builder);
            index = _contents.Count - 1;
            events.Add(new ReasoningStartedEvent(index));
        }

        var reasoningBuilder = (ReasoningBuilder)_contents[index];

        if (text.Length > 0)
        {
            reasoningBuilder.Text.Append(text);
            events.Add(new ReasoningDeltaEvent(index, text));
        }

        if (!string.IsNullOrEmpty(signature))
        {
            reasoningBuilder.Signature = signature;
        }
    }

    private void ProcessToolCall(MistralToolCall toolCall)
    {
        var toolIndex = toolCall.Index ?? 0;

        if (!_toolCallContentIndices.TryGetValue(toolIndex, out var contentIndex))
        {
            var builder = new ToolCallBuilder();
            _contents.Add(builder);
            contentIndex = _contents.Count - 1;
            _toolCallContentIndices[toolIndex] = contentIndex;
        }

        var toolCallBuilder = (ToolCallBuilder)_contents[contentIndex];

        if (!string.IsNullOrEmpty(toolCall.Id))
        {
            toolCallBuilder.CallId = toolCall.Id;
        }

        if (!string.IsNullOrEmpty(toolCall.Function.Name))
        {
            toolCallBuilder.ToolName = toolCall.Function.Name;
        }

        if (toolCall.Function.Arguments.ValueKind == JsonValueKind.String)
        {
            toolCallBuilder.Arguments.Append(toolCall.Function.Arguments.GetString());
        }
        else if (toolCall.Function.Arguments.ValueKind == JsonValueKind.Object)
        {
            toolCallBuilder.Arguments.Append(toolCall.Function.Arguments.GetRawText());
        }
    }

    private void Finalize(List<ChatStreamEvent> events)
    {
        if (_isFinalized)
        {
            return;
        }

        _isFinalized = true;

        for (var index = 0; index < _contents.Count; index++)
        {
            switch (_contents[index])
            {
                case TextBuilder text:
                    events.Add(new TextEndedEvent(index, text.Text.ToString()));
                    break;
                case ReasoningBuilder reasoning:
                    events.Add(new ReasoningEndedEvent(index, reasoning.Text.ToString(), reasoning.Signature));
                    break;
                case ToolCallBuilder toolCall:
                    events.Add(new ToolCallCompletedEvent(index, toolCall.BuildToolCall()));
                    break;
            }
        }
    }

    private int FindLastIndex<T>() where T : ContentBuilder
    {
        for (var index = _contents.Count - 1; index >= 0; index--)
        {
            if (_contents[index] is T)
            {
                return index;
            }
        }

        return -1;
    }

    private static string ExtractThinkingText(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("thinking", out var thinking) || thinking.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var item in thinking.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) &&
                type.GetString() == "text" &&
                item.TryGetProperty("text", out var text))
            {
                builder.Append(text.GetString());
            }
        }

        return builder.ToString();
    }

    private static ImageContent? MapImage(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("image_url", out var imageUrl))
        {
            return null;
        }

        if (imageUrl.ValueKind == JsonValueKind.String)
        {
            return new ImageContent
            {
                Url = imageUrl.GetString() ?? string.Empty
            };
        }

        if (imageUrl.ValueKind == JsonValueKind.Object &&
            imageUrl.TryGetProperty("url", out var url))
        {
            var detail = imageUrl.TryGetProperty("detail", out var detailProperty)
                ? detailProperty.GetString()
                : null;

            return new ImageContent
            {
                Url = url.GetString() ?? string.Empty,
                Detail = detail
            };
        }

        return null;
    }

    private static ChatFinishReason MapFinishReason(string finishReason)
    {
        return finishReason switch
        {
            "stop" => ChatFinishReason.Stop,
            "length" => ChatFinishReason.Length,
            "model_length" => ChatFinishReason.Length,
            "tool_calls" => ChatFinishReason.ToolCalls,
            "error" => ChatFinishReason.Error,
            _ => ChatFinishReason.Unknown
        };
    }

    private abstract class ContentBuilder
    {
        public abstract ChatContent Build();
    }

    private sealed class TextBuilder : ContentBuilder
    {
        public StringBuilder Text { get; } = new();

        public override ChatContent Build()
        {
            return new TextContent
            {
                Text = Text.ToString()
            };
        }
    }

    private sealed class ReasoningBuilder : ContentBuilder
    {
        public StringBuilder Text { get; } = new();
        public string? Signature { get; set; }

        public override ChatContent Build()
        {
            return new ReasoningContent
            {
                Text = Text.ToString(),
                Signature = Signature
            };
        }
    }

    private sealed class ImageBuilder(ImageContent image) : ContentBuilder
    {
        public override ChatContent Build()
        {
            return image;
        }
    }

    private sealed class ToolCallBuilder : ContentBuilder
    {
        public string? CallId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder Arguments { get; } = new();

        public ToolCallContent BuildToolCall()
        {
            var rawArguments = Arguments.ToString();
            JsonElement arguments;

            if (string.IsNullOrWhiteSpace(rawArguments))
            {
                arguments = EmptyArguments;
            }
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(rawArguments);
                    arguments = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    arguments = EmptyArguments;
                }
            }

            return new ToolCallContent
            {
                CallId = CallId ?? string.Empty,
                ToolName = ToolName ?? string.Empty,
                Arguments = arguments
            };
        }

        public override ChatContent Build()
        {
            return BuildToolCall();
        }
    }
}

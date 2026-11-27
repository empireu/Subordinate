using System.Text;
using System.Text.Json.Nodes;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace KnowledgeSystem.Ai.Providers.Protocols.Mistral;

internal static class MistralRequestMapper
{
    public static MistralChatRequest Map(ChatRequest request)
    {
        var messages = new List<MistralMessage>(request.Messages.Count);

        foreach (var message in request.Messages)
        {
            messages.Add(MapMessage(message));
        }

        var tools = MapTools(request.Tools);

        return new MistralChatRequest
        {
            Model = request.Model ?? string.Empty,
            Messages = messages,
            Stream = true,
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens,
            Tools = tools,
            ToolChoice = request.ToolChoice,
            ReasoningEffort = request.ReasoningEffort,
            ParallelToolCalls = tools is { Count: > 0 } ? true : null,
            Metadata = request.Metadata is { Count: > 0 } ? request.Metadata : null,
            StreamOptions = new MistralStreamOptions
            {
                IncludeUsage = true
            }
        };
    }

    private static MistralMessage MapMessage(ChatMessage message)
    {
        return message.Role switch
        {
            ChatRole.System => MapSystemMessage(message),
            ChatRole.User => MapUserMessage(message),
            ChatRole.Assistant => MapAssistantMessage(message),
            ChatRole.Tool => MapToolMessage(message),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Role, "Unsupported chat role.")
        };
    }

    private static MistralMessage MapSystemMessage(ChatMessage message)
    {
        return new MistralMessage
        {
            Role = "system",
            Content = JsonValue.Create(JoinText(message.Contents))
        };
    }

    private static MistralMessage MapUserMessage(ChatMessage message)
    {
        return new MistralMessage
        {
            Role = "user",
            Content = MapUserContent(message.Contents)
        };
    }

    private static MistralMessage MapAssistantMessage(ChatMessage message)
    {
        var toolCalls = new List<MistralToolCall>();
        var content = MapAssistantContent(message.Contents, toolCalls);

        return new MistralMessage
        {
            Role = "assistant",
            Content = content,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
    }

    private static MistralMessage MapToolMessage(ChatMessage message)
    {
        var result = message.Contents.OfType<ToolResultContent>().FirstOrDefault()
            ?? throw new InvalidOperationException("Tool message must contain a tool result.");

        return new MistralMessage
        {
            Role = "tool",
            Content = HasContentAfterToolResult(message.Contents) ? MapToolResultContent(message.Contents, result) : JsonValue.Create(result.Output),
            ToolCallId = result.CallId,
            Name = result.ToolName
        };
    }

    private static bool HasContentAfterToolResult(IReadOnlyList<ChatContent> contents)
    {
        var sawToolResult = false;

        foreach (var content in contents)
        {
            if (content is ToolResultContent)
            {
                sawToolResult = true;
                continue;
            }

            if (sawToolResult)
            {
                return true;
            }
        }

        return false;
    }

    private static JsonArray MapToolResultContent(IReadOnlyList<ChatContent> contents, ToolResultContent result)
    {
        var array = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = result.Output
            }
        };

        var sawToolResult = false;

        foreach (var content in contents)
        {
            if (content is ToolResultContent)
            {
                sawToolResult = true;
                continue;
            }

            if (!sawToolResult)
            {
                continue;
            }

            switch (content)
            {
                case TextContent text:
                {
                    array.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text.Text
                    });
                    break;
                }
                case ImageContent image:
                {
                    array.Add(MapImage(image));
                    break;
                }
            }
        }

        return array;
    }

    private static JsonNode? MapUserContent(IReadOnlyList<ChatContent> contents)
    {
        var hasImage = contents.Any(content => content is ImageContent);

        if (!hasImage)
        {
            return JsonValue.Create(JoinText(contents));
        }

        var array = new JsonArray();

        foreach (var content in contents)
        {
            switch (content)
            {
                case TextContent text:
                    array.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text.Text
                    });
                    break;
                case ImageContent image:
                    array.Add(MapImage(image));
                    break;
            }
        }

        return array;
    }

    private static JsonNode? MapAssistantContent(
        IReadOnlyList<ChatContent> contents,
        List<MistralToolCall> toolCalls
    )
    {
        var hasReasoning = contents.Any(content => content is ReasoningContent);
        var hasText = contents.Any(content => content is TextContent);

        foreach (var content in contents)
        {
            if (content is ToolCallContent toolCall)
            {
                toolCalls.Add(new MistralToolCall
                {
                    Id = toolCall.CallId,
                    Function = new MistralFunctionCall
                    {
                        Name = toolCall.ToolName,
                        Arguments = toolCall.Arguments
                    }
                });
            }
        }

        if (!hasReasoning)
        {
            return hasText ? JsonValue.Create(JoinText(contents)) : null;
        }

        var array = new JsonArray();

        foreach (var content in contents)
        {
            switch (content)
            {
                case ReasoningContent reasoning:
                    array.Add(MapReasoning(reasoning));
                    break;
                case TextContent text:
                    array.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text.Text
                    });
                    break;
            }
        }

        return array;
    }

    private static JsonObject MapReasoning(ReasoningContent reasoning)
    {
        var thinking = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = reasoning.Text ?? string.Empty
            }
        };

        var result = new JsonObject
        {
            ["type"] = "thinking",
            ["thinking"] = thinking
        };

        if (!string.IsNullOrEmpty(reasoning.Signature))
        {
            result["signature"] = reasoning.Signature;
        }

        return result;
    }

    private static JsonObject MapImage(ImageContent image)
    {
        var imageUrl = new JsonObject
        {
            ["url"] = image.Url
        };

        if (!string.IsNullOrEmpty(image.Detail))
        {
            imageUrl["detail"] = image.Detail;
        }

        return new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = imageUrl
        };
    }

    private static List<MistralTool>? MapTools(IReadOnlyList<ChatTool>? tools)
    {
        if (tools == null || tools.Count == 0)
        {
            return null;
        }

        var result = new List<MistralTool>(tools.Count);

        foreach (var tool in tools)
        {
            result.Add(new MistralTool
            {
                Function = new MistralFunctionDefinition
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = tool.ParametersSchema,
                    Strict = tool.Strict
                }
            });
        }

        return result;
    }

    private static string JoinText(IReadOnlyList<ChatContent> contents)
    {
        var builder = new StringBuilder();

        foreach (var content in contents)
        {
            if (content is TextContent text)
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }
}

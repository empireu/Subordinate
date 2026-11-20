using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Configuration;

namespace Subordinate.Tests;

public sealed class LiveImageFactAttribute : FactAttribute
{
    public const string ModelVariable = "SUBORDINATE_LIVE_IMAGE_MODEL";

    public LiveImageFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelVariable)))
        {
            Skip = $"Set {ModelVariable} to run the live image probe.";
        }
    }
}

/// <summary>
///     Checks against the real service that a model sees an image inside a tool message.
/// </summary>
public class ImageLiveProbeTests
{
    [LiveImageFact]
    public async Task TheModelSeesAnImageInAToolMessage()
    {
        var modelId = Environment.GetEnvironmentVariable(LiveImageFactAttribute.ModelVariable)!;
        var providerEntry = new ProviderEntry
        {
            Endpoint = "https://api.mistral.ai/v1",
            ApiKeyEnv = "MISTRAL_API_KEY"
        };
        var modelEntry = new ModelEntry { Provider = "mistral", Model = modelId };
        var provider = SubordinateConfig.CreateProvider("mistral", providerEntry, modelEntry);
        var imageBytes = ImageTestData.SolidRedPng16();
        var request = new ChatRequest
        {
            Model = modelId,
            MaxOutputTokens = 64,
            Messages =
            [
                new ChatMessage
                {
                    Role = ChatRole.System,
                    Contents = [new TextContent { Text = "You are a test subagent." }]
                },
                new ChatMessage
                {
                    Role = ChatRole.User,
                    Contents = [new TextContent { Text = "Read the file probe.png and tell me its main color in one word." }]
                },
                new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Contents =
                    [
                        new ToolCallContent
                        {
                            CallId = "call_probe",
                            ToolName = "read_file",
                            Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["path"] = "probe.png" })
                        }
                    ]
                },
                new ChatMessage
                {
                    Role = ChatRole.Tool,
                    Contents =
                    [
                        new ToolResultContent
                        {
                            CallId = "call_probe",
                            ToolName = "read_file",
                            Output = $"probe.png is a PNG image ({imageBytes.Length} bytes). It is attached below."
                        },
                        ImageContent.FromBytes(imageBytes, "image/png")
                    ]
                }
            ]
        };

        var reply = await provider.CompleteAsync(request, CancellationToken.None);

        Assert.Contains("red", reply.GetText(), StringComparison.OrdinalIgnoreCase);
    }
}

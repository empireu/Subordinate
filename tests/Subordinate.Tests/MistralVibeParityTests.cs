using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Ai.Providers.Protocols.Mistral;
using KnowledgeSystem.Ai.Providers.Transport;
using Xunit;

namespace Subordinate.Tests;

public sealed class MistralVibeParityTests
{
    [Fact]
    public async Task CreateHttpRequest_UsesVibeHeadersAndMetadataWithoutPromptCacheKeyInBody()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var protocolRequest = new ChatProtocolRequest
        {
            Endpoint = new Uri("https://api.mistral.ai/v1"),
            ApiKey = "test-key",
            Headers = new Dictionary<string, string>(),
            Request = new ChatRequest
            {
                Model = "devstral-small-2505",
                Messages =
                [
                    new ChatMessage
                    {
                        Role = ChatRole.User,
                        Contents = [new TextContent { Text = "hello" }]
                    }
                ],
                Tools =
                [
                    new ChatTool
                    {
                        Name = "read_file",
                        Description = "Reads a file.",
                        ParametersSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement
                    }
                ],
                PromptCacheKey = sessionId,
                Metadata = new Dictionary<string, string>
                {
                    ["call_source"] = "vibe_code",
                    ["host_kind"] = "local",
                    ["call_type"] = "main_call",
                    ["session_id"] = sessionId
                }
            }
        };

        using var httpRequest = MistralChatCompletionsProtocol.CreateHttpRequest(protocolRequest);

        Assert.Equal(MistralChatCompletionsProtocol.VibeUserAgent, httpRequest.Headers.GetValues("User-Agent").Single());
        Assert.Equal(sessionId, httpRequest.Headers.GetValues(MistralChatCompletionsProtocol.AffinityHeaderName).Single());

        var bodyJson = await httpRequest.Content!.ReadAsStringAsync();
        var body = JsonNode.Parse(bodyJson)!.AsObject();

        Assert.False(body.ContainsKey("prompt_cache_key"));
        Assert.True(body["parallel_tool_calls"]?.GetValue<bool>());
        Assert.Equal("vibe_code", body["metadata"]?["call_source"]?.GetValue<string>());
        Assert.Equal("local", body["metadata"]?["host_kind"]?.GetValue<string>());
        Assert.Equal("main_call", body["metadata"]?["call_type"]?.GetValue<string>());
        Assert.Equal(sessionId, body["metadata"]?["session_id"]?.GetValue<string>());
    }

    [Fact]
    public void CreateHttpRequest_RespectsCustomHeaderOverrides()
    {
        var protocolRequest = new ChatProtocolRequest
        {
            Endpoint = new Uri("https://api.mistral.ai/v1"),
            ApiKey = "test-key",
            Headers = new Dictionary<string, string>
            {
                ["user-agent"] = "Custom-Agent/1.0",
                ["x-affinity"] = "custom-affinity"
            },
            Request = new ChatRequest
            {
                Model = "devstral-small-2505",
                Messages =
                [
                    new ChatMessage
                    {
                        Role = ChatRole.User,
                        Contents = [new TextContent { Text = "hello" }]
                    }
                ],
                PromptCacheKey = "ignored-affinity"
            }
        };

        using var httpRequest = MistralChatCompletionsProtocol.CreateHttpRequest(protocolRequest);

        Assert.Equal("Custom-Agent/1.0", httpRequest.Headers.GetValues("User-Agent").Single());
        Assert.Equal("custom-affinity", httpRequest.Headers.GetValues("x-affinity").Single());
    }
}

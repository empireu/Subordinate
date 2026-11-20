using System.Text.Json;
using KnowledgeSystem.Agents.Orchestration;
using KnowledgeSystem.Agents.Orchestration.Tools;
using KnowledgeSystem.Agents.Tools;
using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Ai.Providers.Protocols.Mistral;
using KnowledgeSystem.Events.Implementation;
using Subordinate.Core.Classification;
using Subordinate.Core.Running;

namespace Subordinate.Tests;

public class ImageWireFormatTests
{
    private const string ImageToolName = "image_tool";

    private const string PlainToolMessageJson = """{"model":"test-model","messages":[{"role":"tool","content":"line one\n\u0022quoted\u0022 text with a \\ backslash","tool_call_id":"call_1","name":"read_file"}],"stream":true,"stream_options":{"include_usage":true}}""";

    [Fact]
    public void AToolResultWithoutImagesHasAnEmptyImageList()
    {
        var result = new ToolExecutionResult(BuildTool(), true, "done", null, null);

        Assert.Empty(result.Images);
    }

    [Fact]
    public void SuccessKeepsTheImages()
    {
        var images = new[] { new ImageContent { Url = "subordinate-image:abc.png" } };
        var handler = new ImageTestHandler(BuildTool(), images);

        var result = handler.Success("done", images);

        Assert.True(result.IsSuccessful);
        Assert.Same(images, result.Images);
    }

    [Fact]
    public void AnUnsuccessfulResultCannotHoldImages()
    {
        Assert.Throws<ArgumentException>(() => new ToolExecutionResult(BuildTool(), false, null, "failed", null, [new ImageContent { Url = "subordinate-image:abc.png" }]));
    }

    [Fact]
    public void TheBaseContextForwardsAnEmptyImageList()
    {
        var context = new PlainContext();

        context.InsertToolResult("call_1", "read_file", "done", []);

        var message = Assert.Single(context.Messages);
        Assert.Equal(ChatRole.Tool, message.Role);
        Assert.Equal("done", Assert.IsType<ToolResultContent>(Assert.Single(message.Contents)).Output);
    }

    [Fact]
    public void TheBaseContextRejectsImages()
    {
        var context = new PlainContext();

        Assert.Throws<NotSupportedException>(() => context.InsertToolResult("call_1", "read_file", "done", [new ImageContent { Url = "subordinate-image:abc.png" }]));
    }

    [Fact]
    public async Task TheRunnerInsertsTheToolResultFollowedByTheImages()
    {
        var image = new ImageContent { Url = "subordinate-image:abc.png" };
        var agent = new ImageTestAgent([image]);
        var context = new SubordinateContext(NewEnvironment());
        var provider = new ScriptedProvider(ImageCalls("call_1"), SessionHarness.Text(ChatRole.Assistant, "done"));
        var runner = new AgentRunner<SubordinateContext>(provider, agent, null, context, NullEventManager.Instance, CancellationToken.None);

        for (var step = 0; step < 4; step++)
        {
            await runner.ExecuteTurn();
        }

        var message = context.ChatMessages.Single(candidate => candidate.Role == ChatRole.Tool);
        Assert.Collection(
            message.Contents,
            content => Assert.Equal("image result", Assert.IsType<ToolResultContent>(content).Output),
            content => Assert.Same(image, Assert.IsType<ImageContent>(content))
        );
    }

    [Fact]
    public void AToolMessageWithoutImagesMapsToTheSameBytesAsBefore()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents =
            [
                new ToolResultContent
                {
                    CallId = "call_1",
                    ToolName = "read_file",
                    Output = "line one\n\"quoted\" text with a \\ backslash"
                }
            ]
        };

        var json = JsonSerializer.Serialize(MistralRequestMapper.Map(new ChatRequest { Model = "test-model", Messages = [message] }));

        Assert.Equal(PlainToolMessageJson, json);
    }

    [Fact]
    public void AToolMessageWithAnImageMapsToAContentArray()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents =
            [
                new ToolResultContent { CallId = "call_1", ToolName = "read_file", Output = "the image is attached" },
                new ImageContent { Url = "data:image/png;base64,AAAA", Detail = "high" }
            ]
        };

        var json = JsonSerializer.Serialize(MistralRequestMapper.Map(new ChatRequest { Model = "test-model", Messages = [message] }));

        using var document = JsonDocument.Parse(json);
        var mapped = document.RootElement.GetProperty("messages")[0];
        Assert.Equal("tool", mapped.GetProperty("role").GetString());
        Assert.Equal("call_1", mapped.GetProperty("tool_call_id").GetString());
        Assert.Equal("read_file", mapped.GetProperty("name").GetString());

        var content = mapped.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("the image is attached", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AAAA", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("high", content[1].GetProperty("image_url").GetProperty("detail").GetString());
    }

    [Fact]
    public void AToolMessageWithTwoImagesKeepsTheirOrder()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents =
            [
                new ToolResultContent { CallId = "call_1", ToolName = "read_file", Output = "two images" },
                new ImageContent { Url = "data:image/png;base64,AAAA" },
                new ImageContent { Url = "data:image/png;base64,BBBB" }
            ]
        };

        var json = JsonSerializer.Serialize(MistralRequestMapper.Map(new ChatRequest { Model = "test-model", Messages = [message] }));

        using var document = JsonDocument.Parse(json);
        var content = document.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("data:image/png;base64,AAAA", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("data:image/png;base64,BBBB", content[2].GetProperty("image_url").GetProperty("url").GetString());
    }

    private static AgentTool BuildTool()
    {
        return new ToolBuilder(ImageToolName)
            .WithDescription("Returns an image.")
            .Build();
    }

    private static ChatMessage ImageCalls(string callId)
    {
        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new ToolCallContent
                {
                    CallId = callId,
                    ToolName = ImageToolName,
                    Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string>())
                }
            ]
        };
    }

    private static ToolEnvironment NewEnvironment()
    {
        return new ToolEnvironment
        {
            Workdir = Path.Combine(Path.GetTempPath(), "subordinate-tests", "work"),
            Shell = ShellKind.Bash,
            Executor = new FakeExecutor(),
            OutputDirectory = Path.Combine(Path.GetTempPath(), "subordinate-tests", "runs"),
            ReadableRoots = [],
            DefaultShellTimeoutSeconds = 30
        };
    }

    private sealed class PlainContext : AgentExecutionContext
    {
        public List<ChatMessage> Messages { get; } = [];

        public override IReadOnlyList<ChatMessage> ChatMessages => Messages;

        public override void InsertAssistantCompletion(ChatMessage message)
        {
            Messages.Add(message);
        }

        public override void InsertToolResult(string toolCallId, string toolName, string output)
        {
            Messages.Add(new ChatMessage
            {
                Role = ChatRole.Tool,
                Contents = [new ToolResultContent { CallId = toolCallId, ToolName = toolName, Output = output }]
            });
        }
    }

    private sealed class ImageTestAgent : Agent<SubordinateContext>
    {
        public ImageTestAgent(IReadOnlyList<ImageContent> images) : base("image-test")
        {
            var tool = BuildTool();
            ToolRegistry.RegisterTool(tool, new ImageTestHandler(tool, images));
        }
    }

    private sealed class ImageTestHandler(AgentTool tool, IReadOnlyList<ImageContent> images) : ToolHandler<SubordinateContext>.Plain(tool)
    {
        public override Task<ToolExecutionResult> ExecuteAsync(AgentRunner<SubordinateContext> runner, ArgumentExtractionResult args, CancellationToken cancellationToken)
        {
            return Task.FromResult(Success("image result", images));
        }
    }
}

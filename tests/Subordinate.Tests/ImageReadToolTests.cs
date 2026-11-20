using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Images;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;
using Subordinate.Core.Tools;

namespace Subordinate.Tests;

public class ImageReadToolTests
{
    private const string DisabledDescription = "Reads a text file and returns its lines prefixed with line numbers. Use offset and limit to page through large files.";

    private const string EnabledDescription = "Reads a text file and returns its lines prefixed with line numbers, or shows you an image file (PNG, JPEG, GIF or WebP). Use offset and limit to page through large text files; they are ignored for images.";

    [Fact]
    public async Task TheDescriptionIsUnchangedWhenImagesAreDisabled()
    {
        using var harness = new SessionHarness();
        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));

        await harness.RunAsync(provider);

        Assert.Equal(DisabledDescription, ReadTool(provider.FullRequests[0]).Description);
    }

    [Fact]
    public async Task APngFailsWhenTheModelCannotSeeImages()
    {
        using var harness = new SessionHarness();
        WritePng(harness.Workdir, "picture.png");
        File.WriteAllText(Path.Combine(harness.Workdir, "notes.txt"), "hello\n");
        var provider = new ScriptedProvider(ReadCalls(("call_png", "picture.png"), ("call_text", "notes.txt")), SessionHarness.Text(ChatRole.Assistant, "done"));

        await harness.RunAsync(provider);

        var results = SessionHarness.ToolResults(harness.Transcript()).ToList();
        var image = results.Single(result => result.CallId == "call_png");
        Assert.EndsWith("is a PNG image, and the model of this session cannot view images.", image.Output, StringComparison.Ordinal);
        Assert.Contains("1\thello", results.Single(result => result.CallId == "call_text").Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(harness.Paths.ImagesDirectory));
    }

    [Fact]
    public async Task TheDescriptionNamesTheFormatsAndTheLimit()
    {
        using var harness = new SessionHarness();
        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));

        await harness.RunAsync(provider, images: new ImageSettings { Enabled = true });

        Assert.Equal(EnabledDescription, ReadTool(provider.FullRequests[0]).Description);

        using var limited = new SessionHarness();
        var limitedProvider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));

        await limited.RunAsync(limitedProvider, images: new ImageSettings { Enabled = true, MaxImages = 2 });

        var description = ReadTool(limitedProvider.FullRequests[0]).Description;
        Assert.StartsWith(EnabledDescription, description, StringComparison.Ordinal);
        Assert.EndsWith(" At most 2 images can be in the conversation at once. Once that many have been read, reading another image fails.", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadImageReturnsAReferenceASnapshotAndADataUrlOnTheNextRequest()
    {
        using var harness = new SessionHarness();
        var bytes = WritePng(harness.Workdir, "picture.png");
        var provider = new ScriptedProvider(ReadCalls(("call_1", "picture.png")), SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, _) = await harness.RunAsync(provider, images: new ImageSettings { Enabled = true });

        Assert.Equal(SessionStates.Finished, outcome.State);
        var result = SessionHarness.ToolResults(harness.Transcript()).Single(candidate => candidate.CallId == "call_1");
        Assert.False(result.IsError);
        Assert.Equal($"{Path.Combine(harness.Workdir, "picture.png")} is a PNG image ({bytes.Length} bytes). It is attached below.", result.Output);
        var fileName = ImageSnapshots.Hash(bytes) + ".png";
        Assert.True(File.Exists(Path.Combine(harness.Paths.ImagesDirectory, fileName)));

        var toolMessage = provider.Requests[1].Single(message => message.Role == ChatRole.Tool);
        var resultContent = Assert.IsType<ToolResultContent>(toolMessage.Contents[0]);
        Assert.Equal(result.Output, resultContent.Output);
        var image = Assert.IsType<ImageContent>(toolMessage.Contents[1]);
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}", image.Url);
        var storedMessage = harness.Transcript().Single(message => message.Role == ChatRole.Tool);
        Assert.Equal($"subordinate-image:{fileName}", Assert.IsType<ImageContent>(storedMessage.Contents[1]).Url);
    }

    [Fact]
    public async Task DetectionIsByContentAndNotByExtension()
    {
        using var harness = new SessionHarness();
        var bytes = WritePng(harness.Workdir, "picture.txt");
        File.WriteAllText(Path.Combine(harness.Workdir, "notes.png"), "hello\n");
        var provider = new ScriptedProvider(ReadCalls(("call_png", "picture.txt"), ("call_text", "notes.png")), SessionHarness.Text(ChatRole.Assistant, "done"));

        await harness.RunAsync(provider, images: new ImageSettings { Enabled = true });

        var results = SessionHarness.ToolResults(harness.Transcript()).ToList();
        Assert.False(results.Single(result => result.CallId == "call_png").IsError);
        Assert.Contains($"is a PNG image ({bytes.Length} bytes)", results.Single(result => result.CallId == "call_png").Output, StringComparison.Ordinal);
        Assert.Contains("1\thello", results.Single(result => result.CallId == "call_text").Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImageLargerThanTheLimitIsRefusedBeforeItIsRead()
    {
        using var harness = new SessionHarness();
        var path = Path.Combine(harness.Workdir, "huge.png");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.Write(ImageTestData.SolidRedPng16());
            stream.SetLength(ReadFileToolHandler.MaxImageBytes + 1);
        }

        try
        {
            var provider = new ScriptedProvider(ReadCalls(("call_1", "huge.png")), SessionHarness.Text(ChatRole.Assistant, "done"));

            await harness.RunAsync(provider, images: new ImageSettings { Enabled = true });

            var result = SessionHarness.ToolResults(harness.Transcript()).Single(candidate => candidate.CallId == "call_1");
            Assert.Equal($"{path} is 10000001 bytes; images larger than 10000000 bytes cannot be shown.", result.Output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnUnsupportedBinaryFileNamesTheFormats()
    {
        using var harness = new SessionHarness();
        var path = Path.Combine(harness.Workdir, "data.bin");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x00]);

        var provider = new ScriptedProvider(ReadCalls(("call_1", "data.bin")), SessionHarness.Text(ChatRole.Assistant, "done"));

        await harness.RunAsync(provider, images: new ImageSettings { Enabled = true });

        var result = SessionHarness.ToolResults(harness.Transcript()).Single(candidate => candidate.CallId == "call_1");
        Assert.Equal($"{path} looks like a binary file. Supported image formats are PNG, JPEG, GIF and WebP.", result.Output);
    }

    [Fact]
    public async Task AnUnsupportedBinaryFileKeepsTheOldMessageWhenImagesAreDisabled()
    {
        using var harness = new SessionHarness();
        var path = Path.Combine(harness.Workdir, "data.bin");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x00]);

        var provider = new ScriptedProvider(ReadCalls(("call_1", "data.bin")), SessionHarness.Text(ChatRole.Assistant, "done"));

        await harness.RunAsync(provider);

        var result = SessionHarness.ToolResults(harness.Transcript()).Single(candidate => candidate.CallId == "call_1");
        Assert.Equal($"{path} looks like a binary file.", result.Output);
    }

    [Fact]
    public async Task TheLimitRefusesTheThirdSequentialRead()
    {
        using var harness = new SessionHarness();
        WritePng(harness.Workdir, "one.png");
        WriteBluePng(harness.Workdir, "two.png");
        var provider = new ScriptedProvider(
            ReadCalls(("call_1", "one.png")),
            ReadCalls(("call_2", "two.png")),
            ReadCalls(("call_3", "one.png")),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        await harness.RunAsync(provider, images: new ImageSettings { Enabled = true, MaxImages = 2 });

        var results = SessionHarness.ToolResults(harness.Transcript()).ToList();
        Assert.False(results.Single(result => result.CallId == "call_1").IsError);
        Assert.False(results.Single(result => result.CallId == "call_2").IsError);
        Assert.Equal("Maximum number of images in context reached.", results.Single(result => result.CallId == "call_3").Output);
    }

    [Fact]
    public async Task TheLimitCountsTheReadsOfOneBatch()
    {
        using var harness = new SessionHarness();
        WritePng(harness.Workdir, "one.png");
        WriteBluePng(harness.Workdir, "two.png");
        var provider = new ScriptedProvider(
            ReadCalls(("call_1", "one.png"), ("call_2", "two.png"), ("call_3", "one.png")),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        await harness.RunAsync(provider, images: new ImageSettings { Enabled = true, MaxImages = 2 });

        var results = SessionHarness.ToolResults(harness.Transcript()).ToList();
        Assert.False(results.Single(result => result.CallId == "call_1").IsError);
        Assert.False(results.Single(result => result.CallId == "call_2").IsError);
        Assert.Equal("Maximum number of images in context reached.", results.Single(result => result.CallId == "call_3").Output);
    }

    [Fact]
    public async Task TheSameImageIsStoredOnceAndCountsTwice()
    {
        using var harness = new SessionHarness();
        var bytes = WritePng(harness.Workdir, "one.png");
        var provider = new ScriptedProvider(
            ReadCalls(("call_1", "one.png"), ("call_2", "one.png")),
            ReadCalls(("call_3", "one.png")),
            SessionHarness.Text(ChatRole.Assistant, "done"));

        await harness.RunAsync(provider, images: new ImageSettings { Enabled = true, MaxImages = 2 });

        Assert.Single(Directory.GetFiles(harness.Paths.ImagesDirectory));
        Assert.Equal(ImageSnapshots.Hash(bytes) + ".png", Path.GetFileName(Directory.GetFiles(harness.Paths.ImagesDirectory)[0]));
        var results = SessionHarness.ToolResults(harness.Transcript()).ToList();
        Assert.False(results.Single(result => result.CallId == "call_1").IsError);
        Assert.False(results.Single(result => result.CallId == "call_2").IsError);
        Assert.Equal("Maximum number of images in context reached.", results.Single(result => result.CallId == "call_3").Output);
    }

    private static ChatTool ReadTool(ChatRequest request)
    {
        return request.Tools!.Single(tool => tool.Name == ReadFileToolHandler.ToolName);
    }

    private static byte[] WritePng(string directory, string name)
    {
        var bytes = ImageTestData.SolidRedPng16();
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
        return bytes;
    }

    private static byte[] WriteBluePng(string directory, string name)
    {
        var bytes = ImageTestData.SolidPng(8, 8, 0, 0, 255);
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
        return bytes;
    }

    private static ChatMessage ReadCalls(params (string CallId, string Path)[] calls)
    {
        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Contents = calls.Select(call => (ChatContent)new ToolCallContent
            {
                CallId = call.CallId,
                ToolName = ReadFileToolHandler.ToolName,
                Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["path"] = call.Path })
            }).ToList()
        };
    }
}

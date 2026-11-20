using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Ai.Providers.Protocols.Mistral;
using Subordinate.Core.Configuration;
using Subordinate.Core.Images;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;
using Subordinate.Core.Tools;

namespace Subordinate.Tests;

public class ImageRunnerTests
{
    [Fact]
    public async Task AnInterruptedBatchPutsTheImageIntoTheToolMessage()
    {
        using var harness = new SessionHarness();
        var bytes = ImageTestData.SolidRedPng16();
        var reference = ImageSnapshots.Save(harness.Paths.ImagesDirectory, bytes, ImageFormat.Png);
        var text = $"picture.png is a PNG image ({bytes.Length} bytes). It is attached below.";
        var record = harness.Load();
        var assistantIndex = record.Transcript.Count;
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [ReadCalls(("call_1", "picture.png"))]);
        record.Inflight = new InflightRecord
        {
            AssistantIndex = assistantIndex,
            Frames = [new InflightFrameRecord { CallId = "call_1", State = FrameStates.Succeeded, Text = text, Images = [reference] }]
        };
        harness.Store.Save(harness.Paths, record);
        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, _) = await harness.RunAsync(provider, images: new ImageSettings { Enabled = true });

        Assert.Equal(SessionStates.Finished, outcome.State);
        var sent = provider.Requests[0].Single(message => message.Role == ChatRole.Tool);
        Assert.Equal(text, Assert.IsType<ToolResultContent>(sent.Contents[0]).Output);
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}", Assert.IsType<ImageContent>(sent.Contents[1]).Url);
        var stored = harness.Transcript().Single(message => message.Role == ChatRole.Tool);
        Assert.Equal(reference, Assert.IsType<ImageContent>(stored.Contents[1]).Url);
    }

    [Fact]
    public async Task AnApprovedImageReadIsDeliveredWithItsImage()
    {
        using var harness = new SessionHarness();
        var bytes = ImageTestData.SolidRedPng16();
        var outsidePath = Path.Combine(Path.GetDirectoryName(harness.Workdir)!, "outside.png");
        File.WriteAllBytes(outsidePath, bytes);
        var first = new ScriptedProvider(ReadCalls(("call_1", outsidePath)), SessionHarness.Text(ChatRole.Assistant, "waiting"));

        var (firstOutcome, firstRecord) = await harness.RunAsync(first, images: new ImageSettings { Enabled = true });

        Assert.Equal(SessionStates.AwaitingApproval, firstOutcome.State);
        var pending = Assert.Single(firstRecord.Pending);
        Assert.Equal(PendingStatuses.Pending, pending.Status);

        harness.Update(change =>
        {
            var item = change.Pending.Single();
            item.Status = PendingStatuses.Approved;
            item.Decision = new DecisionRecord { Scope = "once" };
        });

        var second = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
        var (secondOutcome, _) = await harness.RunAsync(second, images: new ImageSettings { Enabled = true });

        Assert.Equal(SessionStates.Finished, secondOutcome.State);
        var update = second.Requests[0].Last(message => message.Role == ChatRole.User);
        Assert.Contains("[Supervisor update]", update.GetText(), StringComparison.Ordinal);
        Assert.Contains("It is attached below.", update.GetText(), StringComparison.Ordinal);
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}", Assert.IsType<ImageContent>(update.Contents[^1]).Url);
        var mapped = JsonSerializer.Serialize(MistralRequestMapper.Map(second.FullRequests[0]));
        Assert.Contains("\"image_url\"", mapped, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(mapped);
        var urls = document.RootElement.GetProperty("messages").EnumerateArray()
            .Where(message => message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            .SelectMany(message => message.GetProperty("content").EnumerateArray())
            .Where(chunk => chunk.TryGetProperty("type", out var type) && type.GetString() == "image_url")
            .Select(chunk => chunk.GetProperty("image_url").GetProperty("url").GetString())
            .ToList();
        Assert.Contains($"data:image/png;base64,{Convert.ToBase64String(bytes)}", urls);
    }

    [Fact]
    public async Task ApprovedImageReadsInOneDeliveryRespectTheLimit()
    {
        using var harness = new SessionHarness();
        var bytes = ImageTestData.SolidRedPng16();
        var firstPath = Path.Combine(Path.GetDirectoryName(harness.Workdir)!, "outside-first.png");
        var secondPath = Path.Combine(Path.GetDirectoryName(harness.Workdir)!, "outside-second.png");
        File.WriteAllBytes(firstPath, bytes);
        File.WriteAllBytes(secondPath, bytes);
        var settings = new ImageSettings { Enabled = true, MaxImages = 1 };
        var first = new ScriptedProvider(ReadCalls(("call_1", firstPath), ("call_2", secondPath)), SessionHarness.Text(ChatRole.Assistant, "waiting"));

        var (firstOutcome, firstRecord) = await harness.RunAsync(first, images: settings);

        Assert.Equal(SessionStates.AwaitingApproval, firstOutcome.State);
        Assert.Equal(2, firstRecord.Pending.Count);

        harness.Update(change =>
        {
            foreach (var item in change.Pending)
            {
                item.Status = PendingStatuses.Approved;
                item.Decision = new DecisionRecord { Scope = "once" };
            }
        });

        var second = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
        var (secondOutcome, _) = await harness.RunAsync(second, images: settings);

        Assert.Equal(SessionStates.Finished, secondOutcome.State);
        var update = second.Requests[0].Last(message => message.Role == ChatRole.User);
        Assert.Contains("Maximum number of images in context reached.", update.GetText(), StringComparison.Ordinal);
        Assert.Single(update.Contents.OfType<ImageContent>());
    }

    [Fact]
    public async Task ASavedApprovedResultKeepsItsImage()
    {
        using var harness = new SessionHarness();
        var bytes = ImageTestData.SolidRedPng16();
        var reference = ImageSnapshots.Save(harness.Paths.ImagesDirectory, bytes, ImageFormat.Png);
        var record = harness.Load();
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript,
        [
            ReadCalls(("call_1", "picture.png")),
            ToolResult("call_1", "read_file", "PENDING APPROVAL A1: read picture.png")
        ]);
        record.Pending.Add(new PendingRecord
        {
            Id = "A1",
            CallId = "call_1",
            Tool = ReadFileToolHandler.ToolName,
            Status = PendingStatuses.Approved,
            Summary = "read picture.png",
            DeferralMessage = "PENDING APPROVAL A1: read picture.png",
            ArgumentsJson = """{"path":"picture.png"}""",
            Decision = new DecisionRecord { Scope = "once" },
            Execution = new ExecutionRecord
            {
                State = FrameStates.Succeeded,
                At = DateTimeOffset.UtcNow,
                Output = "picture.png is a PNG image. It is attached below.",
                Images = [reference]
            }
        });
        harness.Store.Save(harness.Paths, record);
        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, _) = await harness.RunAsync(provider, images: new ImageSettings { Enabled = true });

        Assert.Equal(SessionStates.Finished, outcome.State);
        var update = provider.Requests[0].Last(message => message.Role == ChatRole.User);
        Assert.Contains("It is attached below.", update.GetText(), StringComparison.Ordinal);
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}", Assert.IsType<ImageContent>(update.Contents[^1]).Url);
    }

    [Fact]
    public async Task AResumeWithoutSupportsImagesFailsAndKeepsTheTranscript()
    {
        using var harness = new SessionHarness();
        File.WriteAllBytes(Path.Combine(harness.Workdir, "picture.png"), ImageTestData.SolidRedPng16());
        var first = new ScriptedProvider(ReadCalls(("call_1", "picture.png")), SessionHarness.Text(ChatRole.Assistant, "done"));
        await harness.RunAsync(first, images: new ImageSettings { Enabled = true });
        var before = File.ReadAllBytes(harness.Paths.TranscriptFile);
        var resumeProvider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, _) = await harness.RunAsync(resumeProvider);

        Assert.Equal(SessionStates.Failed, outcome.State);
        Assert.Equal("config_error", outcome.Error?.Code);
        Assert.Contains("no longer sets supports_images", outcome.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(resumeProvider.Requests);
        Assert.Equal(before, File.ReadAllBytes(harness.Paths.TranscriptFile));
    }

    [Fact]
    public async Task AResumedSessionSendsTheSnapshotBytesAfterTheFileChanged()
    {
        using var harness = new SessionHarness();
        var path = Path.Combine(harness.Workdir, "picture.png");
        var bytes = ImageTestData.SolidRedPng16();
        File.WriteAllBytes(path, bytes);
        var first = new ScriptedProvider(ReadCalls(("call_1", "picture.png")), SessionHarness.Text(ChatRole.Assistant, "done"));
        await harness.RunAsync(first, images: new ImageSettings { Enabled = true });

        File.WriteAllBytes(path, ImageTestData.SolidPng(4, 4, 0, 0, 255));

        var second = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
        await harness.RunAsync(second, images: new ImageSettings { Enabled = true });

        var firstUrl = FirstToolImageUrl(first.Requests[1]);
        var secondUrl = FirstToolImageUrl(second.Requests[0]);
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}", firstUrl);
        Assert.Equal(firstUrl, secondUrl);
    }

    [Fact]
    public async Task ACompactionReplacesTheKeptImagesAndTheResumeSendsTheSameMessages()
    {
        using var compacted = new SessionHarness();
        File.WriteAllBytes(Path.Combine(compacted.Workdir, "picture.png"), ImageTestData.SolidRedPng16());
        SetContextTokens(compacted, 5000);
        var failing = new ScriptedProvider(ReadCalls(("call_1", "picture.png")), SessionHarness.Text(ChatRole.Assistant, "the summary"));
        var (failingOutcome, failingRecord) = await compacted.RunAsync(failing, images: new ImageSettings { Enabled = true }, compaction: Settings(1, 4000));

        Assert.Equal(SessionStates.Failed, failingOutcome.State);
        Assert.Equal(1, failingRecord.Compactions);
        Assert.Equal(3, failing.Requests.Count);
        var afterCompaction = failing.Requests[2].Single(message => message.Role == ChatRole.Tool);
        Assert.Equal(CompactionImages.RemovedNote, Assert.IsType<TextContent>(afterCompaction.Contents[1]).Text);
        Assert.Contains("subordinate-image:", File.ReadAllText(compacted.Paths.TranscriptFile), StringComparison.Ordinal);

        var resumeProvider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
        var (resumeOutcome, _) = await compacted.RunAsync(resumeProvider, images: new ImageSettings { Enabled = true });

        Assert.Equal(SessionStates.Finished, resumeOutcome.State);
        var mappedAfterCompaction = JsonSerializer.Serialize(MistralRequestMapper.Map(new ChatRequest { Model = "test", Messages = failing.Requests[2] }));
        var mappedAfterResume = JsonSerializer.Serialize(MistralRequestMapper.Map(new ChatRequest { Model = "test", Messages = resumeProvider.Requests[0] }));
        Assert.Equal(mappedAfterCompaction, mappedAfterResume);
    }

    [Fact]
    public async Task TheSummaryRequestNamesTheImagesOnlyWhenThereAreSome()
    {
        using var withImage = new SessionHarness();
        File.WriteAllBytes(Path.Combine(withImage.Workdir, "picture.png"), ImageTestData.SolidRedPng16());
        SetContextTokens(withImage, 5000);
        var imageProvider = new ScriptedProvider(ReadCalls(("call_1", "picture.png")), SessionHarness.Text(ChatRole.Assistant, "the summary"), SessionHarness.Text(ChatRole.Assistant, "done"));
        await withImage.RunAsync(imageProvider, images: new ImageSettings { Enabled = true }, compaction: Settings(1, 4000));

        var imageRequest = imageProvider.Requests[1].Single(message => message.AuthorName == CompactionAuthors.Request);
        Assert.EndsWith(CompactionPrompts.DefaultRequest + "\n\n" + CompactionPrompts.ImagesRequestParagraph, imageRequest.GetText(), StringComparison.Ordinal);

        using var withoutImage = new SessionHarness();
        SetContextTokens(withoutImage, 5000);
        var plainProvider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "the summary"), SessionHarness.Text(ChatRole.Assistant, "done"));
        await withoutImage.RunAsync(plainProvider, compaction: Settings(1, 4000));

        var plainRequest = plainProvider.Requests[1].Single(message => message.AuthorName == CompactionAuthors.Request);
        Assert.Equal(CompactionPrompts.DefaultRequest, plainRequest.GetText());
    }

    [Fact]
    public async Task AnImageRefusedAtTheLimitIsReadAfterACompaction()
    {
        using var harness = new SessionHarness();
        File.WriteAllBytes(Path.Combine(harness.Workdir, "one.png"), ImageTestData.SolidRedPng16());
        File.WriteAllBytes(Path.Combine(harness.Workdir, "two.png"), ImageTestData.SolidPng(8, 8, 0, 0, 255));
        var provider = new ScriptedProvider(
            ReadCalls(("call_1", "one.png")),
            ReadCalls(("call_2", "two.png")),
            SessionHarness.Text(ChatRole.Assistant, "the summary"),
            ReadCalls(("call_3", "two.png")),
            SessionHarness.Text(ChatRole.Assistant, "done"))
        {
            Usages = new Queue<ChatUsage?>([new ChatUsage { InputTokens = 2000, OutputTokens = 10 }, new ChatUsage { InputTokens = 5000, OutputTokens = 10 }, null, null, null])
        };

        var (outcome, record) = await harness.RunAsync(provider, images: new ImageSettings { Enabled = true, MaxImages = 1 }, compaction: Settings(0, 4000));

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(1, record.Compactions);
        Assert.Contains("is a PNG image", SessionHarness.Describe(provider.Requests[1]), StringComparison.Ordinal);
        Assert.Contains("Maximum number of images in context reached.", SessionHarness.Describe(provider.Requests[2]), StringComparison.Ordinal);
        var results = SessionHarness.ToolResults(harness.Transcript()).ToList();
        Assert.Contains("is a PNG image", results.Single(result => result.CallId == "call_3").Output, StringComparison.Ordinal);
        Assert.Contains("\"image_url\"", JsonSerializer.Serialize(MistralRequestMapper.Map(provider.FullRequests[^1])), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOldSessionRecordWithoutTheImageFieldsStillLoads()
    {
        using var harness = new SessionHarness();
        File.WriteAllText(harness.Paths.RecordFile, """
            {
              "schema": 1,
              "name": "test",
              "workdir": "D:/work",
              "pending": [
                {
                  "id": "A1",
                  "call_id": "call_1",
                  "tool": "read_file",
                  "summary": "read picture.png",
                  "execution": { "state": "succeeded", "at": "2024-01-02T03:04:05+00:00", "output": "old output" }
                }
              ],
              "inflight": {
                "assistant_index": 1,
                "frames": [ { "call_id": "call_1", "state": "succeeded", "text": "old text" } ]
              }
            }
            """);

        var loaded = harness.Store.Load(harness.Paths);

        Assert.Null(loaded.Pending.Single().Execution!.Images);
        Assert.Equal("old output", loaded.Pending.Single().Execution!.Output);
        Assert.Null(loaded.Inflight!.Frames.Single().Images);
        Assert.Equal("old text", loaded.Inflight.Frames.Single().Text);
    }

    private static string FirstToolImageUrl(IReadOnlyList<ChatMessage> messages)
    {
        return Assert.IsType<ImageContent>(messages.Single(message => message.Role == ChatRole.Tool).Contents[1]).Url;
    }

    private static ChatMessage ToolResult(string callId, string toolName, string output)
    {
        return new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents = [new ToolResultContent { CallId = callId, ToolName = toolName, Output = output }]
        };
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

    private static CompactionSettings Settings(int windDownTurns, int threshold)
    {
        return new CompactionSettings
        {
            Threshold = threshold,
            WindDownTurns = windDownTurns,
            NoticeText = CompactionPrompts.FormatNotice(CompactionPrompts.DefaultNotice, windDownTurns),
            RequestText = CompactionPrompts.DefaultRequest
        };
    }

    private static void SetContextTokens(SessionHarness harness, long tokens)
    {
        harness.Update(record => record.ContextTokens = tokens);
    }
}

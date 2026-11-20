using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Images;
using Subordinate.Core.Running;

namespace Subordinate.Tests;

public class ImageResolverTests
{
    [Fact]
    public async Task AReferenceBecomesTheDataUrlOfTheSnapshot()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var bytes = ImageTestData.SolidRedPng16();
            var reference = ImageSnapshots.Save(directory, bytes, ImageFormat.Png);
            var inner = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
            var logs = new List<string>();
            var resolver = new ImageResolvingChatProvider(inner, directory, logs.Add);

            await resolver.CompleteAsync(Request(ToolMessage(reference, "high")), CancellationToken.None);

            var image = Assert.IsType<ImageContent>(inner.FullRequests[^1].Messages[0].Contents[1]);
            Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}", image.Url);
            Assert.Equal("high", image.Detail);
            Assert.Empty(logs);
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task MessagesWithoutImagesAndOtherUrlsArePassedThrough()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var inner = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
            var logs = new List<string>();
            var resolver = new ImageResolvingChatProvider(inner, directory, logs.Add);
            var plain = SessionHarness.Text(ChatRole.User, "hello");
            var dataImage = new ChatMessage { Role = ChatRole.User, Contents = [new ImageContent { Url = "data:image/png;base64,AAAA" }] };
            var remoteImage = new ChatMessage { Role = ChatRole.User, Contents = [new ImageContent { Url = "https://example.invalid/x.png" }] };
            var request = new ChatRequest { Model = "test", Messages = [plain, dataImage, remoteImage] };

            await resolver.CompleteAsync(request, CancellationToken.None);

            var sent = inner.FullRequests[^1].Messages;
            Assert.Same(plain, sent[0]);
            Assert.Same(dataImage, sent[1]);
            Assert.Same(remoteImage, sent[2]);
            Assert.Empty(logs);
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task TheCacheIsReusedWhileTheRequestKeepsUsingTheImage()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var bytes = ImageTestData.SolidRedPng16();
            var reference = ImageSnapshots.Save(directory, bytes, ImageFormat.Png);
            var inner = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "one"), SessionHarness.Text(ChatRole.Assistant, "two"));
            var logs = new List<string>();
            var resolver = new ImageResolvingChatProvider(inner, directory, logs.Add);
            var request = Request(ToolMessage(reference, null));

            await resolver.CompleteAsync(request, CancellationToken.None);
            File.Delete(Path.Combine(directory, reference[ImageSnapshots.UrlPrefix.Length..]));
            await resolver.CompleteAsync(request, CancellationToken.None);

            var image = Assert.IsType<ImageContent>(inner.FullRequests[^1].Messages[0].Contents[1]);
            Assert.StartsWith("data:image/png;base64,", image.Url, StringComparison.Ordinal);
            Assert.Empty(logs);
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task TheCacheDropsImagesTheNextRequestDoesNotUse()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var bytes = ImageTestData.SolidRedPng16();
            var reference = ImageSnapshots.Save(directory, bytes, ImageFormat.Png);
            var inner = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "one"), SessionHarness.Text(ChatRole.Assistant, "two"), SessionHarness.Text(ChatRole.Assistant, "three"), SessionHarness.Text(ChatRole.Assistant, "four"));
            var logs = new List<string>();
            var resolver = new ImageResolvingChatProvider(inner, directory, logs.Add);

            await resolver.CompleteAsync(Request(ToolMessage(reference, null)), CancellationToken.None);
            File.Delete(Path.Combine(directory, reference[ImageSnapshots.UrlPrefix.Length..]));
            await resolver.CompleteAsync(Request(ToolMessage(reference, null)), CancellationToken.None);
            await resolver.CompleteAsync(Request(SessionHarness.Text(ChatRole.User, "no image this time")), CancellationToken.None);
            await resolver.CompleteAsync(Request(ToolMessage(reference, null)), CancellationToken.None);

            var text = Assert.IsType<TextContent>(inner.FullRequests[^1].Messages[0].Contents[1]);
            Assert.Equal($"[image unavailable: {reference[ImageSnapshots.UrlPrefix.Length..]} could not be loaded from the session directory]", text.Text);
            Assert.Single(logs);
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task AMissingFileBecomesATextNoteAndIsLoggedOnce()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var reference = ImageSnapshots.UrlPrefix + new string('b', ImageSnapshots.HashCharacters) + ".png";
            var inner = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "one"), SessionHarness.Text(ChatRole.Assistant, "two"));
            var logs = new List<string>();
            var resolver = new ImageResolvingChatProvider(inner, directory, logs.Add);
            var request = Request(ToolMessage(reference, null));

            await resolver.CompleteAsync(request, CancellationToken.None);
            await resolver.CompleteAsync(request, CancellationToken.None);

            var text = Assert.IsType<TextContent>(inner.FullRequests[^1].Messages[0].Contents[1]);
            Assert.Equal($"[image unavailable: {reference[ImageSnapshots.UrlPrefix.Length..]} could not be loaded from the session directory]", text.Text);
            var log = Assert.Single(logs);
            Assert.Contains("could not be read", log, StringComparison.Ordinal);
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task AFileWhoseHashDoesNotMatchBecomesATextNoteAndIsLoggedOnce()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var reference = ImageSnapshots.Save(directory, ImageTestData.SolidRedPng16(), ImageFormat.Png);
            var path = Path.Combine(directory, reference[ImageSnapshots.UrlPrefix.Length..]);
            File.WriteAllBytes(path, ImageTestData.SolidPng(4, 4, 0, 0, 255));
            var inner = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "one"), SessionHarness.Text(ChatRole.Assistant, "two"));
            var logs = new List<string>();
            var resolver = new ImageResolvingChatProvider(inner, directory, logs.Add);
            var request = Request(ToolMessage(reference, null));

            await resolver.CompleteAsync(request, CancellationToken.None);
            await resolver.CompleteAsync(request, CancellationToken.None);

            var text = Assert.IsType<TextContent>(inner.FullRequests[^1].Messages[0].Contents[1]);
            Assert.Equal($"[image unavailable: {reference[ImageSnapshots.UrlPrefix.Length..]} could not be loaded from the session directory]", text.Text);
            var log = Assert.Single(logs);
            Assert.Contains("hash", log, StringComparison.Ordinal);
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task AReferenceThatIsNotASnapshotBecomesATextNote()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var inner = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
            var logs = new List<string>();
            var resolver = new ImageResolvingChatProvider(inner, directory, logs.Add);

            await resolver.CompleteAsync(Request(ToolMessage("subordinate-image:../escape.png", null)), CancellationToken.None);

            var text = Assert.IsType<TextContent>(inner.FullRequests[^1].Messages[0].Contents[1]);
            Assert.Contains("[image unavailable:", text.Text, StringComparison.Ordinal);
            Assert.Single(logs);
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task StreamAsyncResolvesTheSameWay()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var bytes = ImageTestData.SolidRedPng16();
            var reference = ImageSnapshots.Save(directory, bytes, ImageFormat.Png);
            var inner = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));
            var logs = new List<string>();
            var resolver = new ImageResolvingChatProvider(inner, directory, logs.Add);

            await foreach (var streamEvent in resolver.StreamAsync(Request(ToolMessage(reference, null)), CancellationToken.None))
            {
                Assert.IsType<StreamCompletedEvent>(streamEvent);
            }

            var image = Assert.IsType<ImageContent>(inner.FullRequests[^1].Messages[0].Contents[1]);
            Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}", image.Url);
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void AnImageCountsAsTheFixedImageTokenEstimate()
    {
        var text = SessionHarness.Text(ChatRole.User, "hello");
        var withImage = new ChatMessage
        {
            Role = ChatRole.User,
            Contents = [new TextContent { Text = "hello" }, new ImageContent { Url = "subordinate-image:" + new string('a', ImageSnapshots.HashCharacters) + ".png" }]
        };

        Assert.Equal(ContextEstimator.Estimate([text]) + ContextEstimator.ImageTokenEstimate, ContextEstimator.Estimate([withImage]));
        Assert.Equal(1500, ContextEstimator.ImageTokenEstimate);
    }

    private static ChatRequest Request(ChatMessage message)
    {
        return new ChatRequest { Model = "test-model", Messages = [message] };
    }

    private static ChatMessage ToolMessage(string url, string? detail)
    {
        return new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents =
            [
                new ToolResultContent { CallId = "call_1", ToolName = "read_file", Output = "the image is attached" },
                new ImageContent { Url = url, Detail = detail }
            ]
        };
    }
}

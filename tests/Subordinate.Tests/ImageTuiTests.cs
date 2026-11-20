using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Images;
using Subordinate.Core.Sessions;
using Subordinate.Tui;
using Subordinate.Tui.Presentation;

namespace Subordinate.Tests;

public class ImageTuiTests
{
    private const string ToolOutput = "picture.png is a PNG image (73 bytes). It is attached below.";

    [Fact]
    public void AToolMessageWithAnImageShowsItsText()
    {
        var state = new HistoryFormatState();
        var message = new ChatMessage
        {
            Role = ChatRole.Tool,
            Contents =
            [
                new ToolResultContent { CallId = "call_1", ToolName = "read_file", Output = ToolOutput },
                new ImageContent { Url = Reference('a') }
            ]
        };

        var blocks = HistoryFormatter.FormatMessage(message, state, false, false);

        Assert.Contains(blocks, block => block.Text.Contains("is a PNG image", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, block => block.Text.Contains(ImageSnapshots.UrlPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void AUserMessageWithAnImageShowsItsText()
    {
        var state = new HistoryFormatState();
        state.SeenUser = true;
        var message = new ChatMessage
        {
            Role = ChatRole.User,
            Contents =
            [
                new TextContent { Text = "[Supervisor update]\nThe image is attached." },
                new ImageContent { Url = Reference('b') }
            ]
        };

        var blocks = HistoryFormatter.FormatMessage(message, state, false, false);

        var body = Assert.Single(blocks, block => block.Role == HistoryBlockRole.Body);
        Assert.Contains("The image is attached.", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintShowsAnImageReferenceWithoutAnError()
    {
        using var home = new TuiTestHome();
        var paths = home.CreateSession("images", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-5));
        var record = home.Store.Load(paths);
        record.Transcript = home.Store.AppendTranscript(paths, record.Transcript,
        [
            new ChatMessage
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new ToolCallContent
                    {
                        CallId = "call_1",
                        ToolName = "read_file",
                        Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["path"] = "picture.png" })
                    }
                ]
            },
            new ChatMessage
            {
                Role = ChatRole.Tool,
                Contents =
                [
                    new ToolResultContent { CallId = "call_1", ToolName = "read_file", Output = ToolOutput },
                    new ImageContent { Url = Reference('c') }
                ]
            },
            new ChatMessage
            {
                Role = ChatRole.User,
                Contents =
                [
                    new TextContent { Text = "[Supervisor update]\nThe approved image is attached." },
                    new ImageContent { Url = Reference('d') }
                ]
            },
            new ChatMessage { Role = ChatRole.Assistant, Contents = [new TextContent { Text = "done" }] }
        ]);
        home.Store.Save(paths, record);
        var writer = new StringWriter();

        var exit = TuiPrint.Run(new TuiOptions { Print = true, Home = home.Home, Session = "images", Width = 120 }, writer);

        Assert.Equal(0, exit);
        var text = writer.ToString();
        Assert.Contains("is a PNG image", text, StringComparison.Ordinal);
        Assert.Contains("The approved image is attached.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ImageSnapshots.UrlPrefix, text, StringComparison.Ordinal);
    }

    private static string Reference(char digit)
    {
        return ImageSnapshots.UrlPrefix + new string(digit, ImageSnapshots.HashCharacters) + ".png";
    }
}

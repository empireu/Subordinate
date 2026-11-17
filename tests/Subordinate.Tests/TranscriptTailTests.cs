using System.Text;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;

namespace Subordinate.Tests;

public class TranscriptTailTests
{
    [Fact]
    public void IncrementalReadsReturnOnlyNewMessages()
    {
        using var harness = new SessionHarness();
        var tail = new TranscriptTail(harness.Paths.TranscriptFile);
        var first = tail.Refresh(harness.Load());

        Assert.Equal(2, first.Messages.Count);
        Assert.True(first.Reset);

        var record = harness.Load();
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.Assistant, "third")]);
        harness.Store.Save(harness.Paths, record);
        var second = tail.Refresh(harness.Load());

        Assert.Single(second.Messages);
        Assert.False(second.Reset);
        Assert.Equal("third", second.Messages[0].GetText());
    }

    [Fact]
    public void BytesPastTheCommittedPointerAreIgnored()
    {
        using var harness = new SessionHarness();
        var tail = new TranscriptTail(harness.Paths.TranscriptFile);
        tail.Refresh(harness.Load());
        File.AppendAllText(harness.Paths.TranscriptFile, "garbage\n");

        var refresh = tail.Refresh(harness.Load());

        Assert.Empty(refresh.Messages);
        Assert.Null(refresh.Notice);
    }

    [Fact]
    public void AnUnreadableLineIsSkippedAndKeepsItsNotice()
    {
        using var home = new TuiTestHome();
        var paths = home.CreateSession("bad-line", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-1), "The task.");
        var tail = new TranscriptTail(paths.TranscriptFile);
        var before = tail.Refresh(home.Store.Load(paths)).Messages.Count;
        home.AppendUnreadableLine(paths);
        home.AppendMessages(paths, SessionHarness.Text(ChatRole.Assistant, "after"));

        var refresh = tail.Refresh(home.Store.Load(paths));
        var idle = tail.Refresh(home.Store.Load(paths));

        Assert.Single(refresh.Messages);
        Assert.Equal("after", refresh.Messages[0].GetText());
        Assert.Equal(before + 2, tail.MessagesRead);
        Assert.Equal("1 transcript line could not be read and is not shown.", refresh.Notice);
        Assert.Empty(idle.Messages);
        Assert.Equal(refresh.Notice, idle.Notice);
    }

    [Fact]
    public void ARecreatedSessionResetsTheTail()
    {
        using var harness = new SessionHarness();
        var tail = new TranscriptTail(harness.Paths.TranscriptFile);
        tail.Refresh(harness.Load());
        var record = harness.Load();
        record.CreatedAt = record.CreatedAt.AddMinutes(1);
        harness.Store.Save(harness.Paths, record);

        var refresh = tail.Refresh(harness.Load());

        Assert.True(refresh.Reset);
        Assert.Equal(2, refresh.Messages.Count);
    }

    [Fact]
    public void ASmallerPointerResetsTheTail()
    {
        using var harness = new SessionHarness();
        var tail = new TranscriptTail(harness.Paths.TranscriptFile);
        tail.Refresh(harness.Load());
        var record = harness.Load();
        var text = File.ReadAllText(harness.Paths.TranscriptFile);
        var firstLineBytes = Encoding.UTF8.GetByteCount(text[..(text.IndexOf('\n') + 1)]);
        record.Transcript = new TranscriptPointer { Count = 1, Bytes = firstLineBytes };
        harness.Store.Save(harness.Paths, record);

        var refresh = tail.Refresh(harness.Load());

        Assert.True(refresh.Reset);
        Assert.Single(refresh.Messages);
    }
}

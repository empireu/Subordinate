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

        Assert.Equal(2, MessagesOf(first).Length);
        Assert.True(first.Reset);

        var record = harness.Load();
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.Assistant, "third")]);
        harness.Store.Save(harness.Paths, record);
        var second = tail.Refresh(harness.Load());

        Assert.Single(MessagesOf(second));
        Assert.False(second.Reset);
        Assert.Equal("third", MessagesOf(second)[0].GetText());
    }

    [Fact]
    public void BytesPastTheCommittedPointerAreIgnored()
    {
        using var harness = new SessionHarness();
        var tail = new TranscriptTail(harness.Paths.TranscriptFile);
        tail.Refresh(harness.Load());
        File.AppendAllText(harness.Paths.TranscriptFile, "garbage\n");

        var refresh = tail.Refresh(harness.Load());

        Assert.Empty(refresh.Entries);
        Assert.Null(refresh.Notice);
    }

    [Fact]
    public void AnUnreadableLineIsSkippedAndKeepsItsNotice()
    {
        using var home = new TuiTestHome();
        var paths = home.CreateSession("bad-line", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-1), "The task.");
        var tail = new TranscriptTail(paths.TranscriptFile);
        var before = tail.Refresh(home.Store.Load(paths)).Entries.Count;
        home.AppendUnreadableLine(paths);
        home.AppendMessages(paths, SessionHarness.Text(ChatRole.Assistant, "after"));

        var refresh = tail.Refresh(home.Store.Load(paths));
        var idle = tail.Refresh(home.Store.Load(paths));

        Assert.Single(MessagesOf(refresh));
        Assert.Equal("after", MessagesOf(refresh)[0].GetText());
        Assert.Equal(before + 2, tail.MessagesRead);
        Assert.Equal("1 transcript line could not be read and is not shown.", refresh.Notice);
        Assert.Empty(idle.Entries);
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
        Assert.Equal(2, MessagesOf(refresh).Length);
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
        Assert.Single(MessagesOf(refresh));
    }

    [Fact]
    public void AMarkerBetweenMessagesIsReturnedAsAnEntry()
    {
        using var harness = new SessionHarness();
        var tail = new TranscriptTail(harness.Paths.TranscriptFile);
        tail.Refresh(harness.Load());
        var record = harness.Load();
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, new CompactionMarker
        {
            At = DateTimeOffset.UtcNow,
            RemovedMessages = 1,
            TokensBefore = 100,
            Message = "replacement",
            KeptFromLine = 1,
            KeptToLine = 2
        });
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.Assistant, "after")]);
        harness.Store.Save(harness.Paths, record);

        var refresh = tail.Refresh(harness.Load());

        Assert.Equal(2, refresh.Entries.Count);
        Assert.IsType<TranscriptCompactionEntry>(refresh.Entries[0]);
        var message = Assert.IsType<TranscriptMessageEntry>(refresh.Entries[1]);
        Assert.Equal("after", message.Message.GetText());
        Assert.Equal(4, tail.MessagesRead);
    }

    private static ChatMessage[] MessagesOf(TranscriptRefresh refresh)
    {
        return refresh.Entries.OfType<TranscriptMessageEntry>().Select(entry => entry.Message).ToArray();
    }
}

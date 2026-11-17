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

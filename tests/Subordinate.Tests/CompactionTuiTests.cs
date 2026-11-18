using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Sessions;
using Subordinate.Tui;
using Subordinate.Tui.Interface;
using Subordinate.Tui.Presentation;

namespace Subordinate.Tests;

public class CompactionTuiTests
{
    [Fact]
    public void AMarkerRendersAsOneCompactionNote()
    {
        var marker = new CompactionMarker
        {
            At = DateTimeOffset.UtcNow,
            RemovedMessages = 412,
            TokensBefore = 401234,
            Message = "replacement text that must stay hidden",
            KeptFromLine = 1,
            KeptToLine = 2
        };

        var blocks = HistoryFormatter.FormatEntry(new TranscriptCompactionEntry(marker), new HistoryFormatState(), false, false);

        Assert.Equal(
        [
            new HistoryBlock(HistoryBlockKind.Compaction, HistoryBlockRole.Spacer, string.Empty),
            new HistoryBlock(HistoryBlockKind.Compaction, HistoryBlockRole.Note, "context compacted, 412 messages removed, about 401234 tokens before")
        ], blocks);
        Assert.DoesNotContain(blocks, block => block.Text.Contains("replacement text that must stay hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void NoticeAndRequestAreCompactionNotesAndDoNotBecomeTheTask()
    {
        var state = new HistoryFormatState();
        var notice = new ChatMessage
        {
            Role = ChatRole.User,
            AuthorName = CompactionAuthors.Notice,
            Contents = [new TextContent { Text = "[Context compaction notice] ..." }]
        };
        var request = new ChatMessage
        {
            Role = ChatRole.User,
            AuthorName = CompactionAuthors.Request,
            Contents = [new TextContent { Text = "[Context compaction request] ..." }]
        };

        var noticeBlocks = HistoryFormatter.FormatMessage(notice, state, false, false);
        var requestBlocks = HistoryFormatter.FormatMessage(request, state, false, false);
        var taskBlocks = HistoryFormatter.FormatMessage(SessionHarness.Text(ChatRole.User, "Do the task."), state, false, false);

        Assert.Equal(
        [
            new HistoryBlock(HistoryBlockKind.Compaction, HistoryBlockRole.Spacer, string.Empty),
            new HistoryBlock(HistoryBlockKind.Compaction, HistoryBlockRole.Note, "compaction notice")
        ], noticeBlocks);
        Assert.Equal(
        [
            new HistoryBlock(HistoryBlockKind.Compaction, HistoryBlockRole.Spacer, string.Empty),
            new HistoryBlock(HistoryBlockKind.Compaction, HistoryBlockRole.Note, "compaction summary requested")
        ], requestBlocks);
        Assert.Equal(HistoryBlockKind.Task, taskBlocks[1].Kind);
        Assert.Equal("task", taskBlocks[1].Text);
    }

    [Fact]
    public void CompactionHasItsOwnColor()
    {
        var header = MonitorColors.ForHistoryLine(HistoryBlockKind.Compaction, HistoryBlockRole.Header);
        var note = MonitorColors.ForHistoryLine(HistoryBlockKind.Compaction, HistoryBlockRole.Note);

        Assert.NotNull(header);
        Assert.NotNull(note);

        foreach (var kind in Enum.GetValues<HistoryBlockKind>().Where(kind => kind != HistoryBlockKind.Compaction))
        {
            Assert.NotEqual(header, MonitorColors.ForHistoryLine(kind, HistoryBlockRole.Header));
            Assert.NotEqual(note, MonitorColors.ForHistoryLine(kind, HistoryBlockRole.Note));
        }
    }

    [Fact]
    public void PrintModeShowsTheCompactionMarker()
    {
        using var home = new TuiTestHome();
        var paths = home.CreateSession("alpha", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-5), "Do the alpha task.");
        var record = home.Store.Load(paths);
        record.Transcript = home.Store.AppendCompaction(paths, record.Transcript, new CompactionMarker
        {
            At = DateTimeOffset.UtcNow,
            RemovedMessages = 7,
            TokensBefore = 12345,
            Message = "replacement",
            KeptFromLine = 1,
            KeptToLine = 2
        });
        home.Store.Save(paths, record);
        var writer = new StringWriter();

        var exit = TuiPrint.Run(new TuiOptions { Print = true, Home = home.Home, Session = "alpha", Width = 120 }, writer);

        Assert.Equal(0, exit);
        Assert.Contains("context compacted, 7 messages removed, about 12345 tokens before", writer.ToString(), StringComparison.Ordinal);
    }
}

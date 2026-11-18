using System.Globalization;
using System.Text;
using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class CompactionTranscriptTests
{
    [Fact]
    public void CompactionMarkerRoundTripsTheKeptRange()
    {
        var marker = new CompactionMarker
        {
            At = DateTimeOffset.Parse("2026-11-18T12:00:00+00:00", CultureInfo.InvariantCulture),
            RemovedMessages = 412,
            TokensBefore = 401234,
            Message = "replacement",
            KeptFromLine = 412,
            KeptToLine = 430
        };

        var line = TranscriptSerializer.SerializeCompaction(marker);
        var entry = Assert.IsType<TranscriptCompactionEntry>(TranscriptSerializer.DeserializeEntry(line));
        var loaded = entry.Marker;

        Assert.Contains("\"kind\":\"compaction\"", line, StringComparison.Ordinal);
        Assert.Contains("\"kept_from_line\":412", line, StringComparison.Ordinal);
        Assert.Contains("\"kept_to_line\":430", line, StringComparison.Ordinal);
        Assert.Equal(marker.At, loaded.At);
        Assert.Equal(412, loaded.RemovedMessages);
        Assert.Equal(401234, loaded.TokensBefore);
        Assert.Equal("replacement", loaded.Message);
        Assert.Equal(412, loaded.KeptFromLine);
        Assert.Equal(430, loaded.KeptToLine);
    }

    [Fact]
    public void MessageLinesDeserializeAsMessageEntries()
    {
        var entry = Assert.IsType<TranscriptMessageEntry>(TranscriptSerializer.DeserializeEntry(TranscriptSerializer.Serialize(SessionHarness.Text(ChatRole.User, "hello"))));

        Assert.Equal(ChatRole.User, entry.Message.Role);
        Assert.Equal("hello", entry.Message.GetText());
    }

    [Fact]
    public void UnknownEntryKindsThrow()
    {
        Assert.Throws<InvalidDataException>(() => TranscriptSerializer.DeserializeEntry("""{"kind":"other"}"""));
    }

    [Fact]
    public void LoadTranscriptLogWithoutMarkersReturnsTheSameMessages()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();

        var log = harness.Store.LoadTranscriptLog(harness.Paths, record.Transcript);

        Assert.Equal(SessionHarness.Describe(harness.Transcript()), SessionHarness.Describe(log.ActiveMessages));
        Assert.Equal(SessionHarness.Describe(log.ActiveMessages), SessionHarness.Describe(log.AllMessages));
        Assert.Equal([0, 1], log.ActiveLineIndices);
        Assert.Equal("Do the task.", log.OriginalTask);
        Assert.Equal(0, log.CompactionCount);
    }

    [Fact]
    public void LoadTranscriptLogUsesTheLastMarkerAndItsKeptRange()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript,
        [
            SessionHarness.Text(ChatRole.Assistant, "before"),
            SessionHarness.Text(ChatRole.User, "tail before the marker")
        ]);
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("replacement text", 2, 4));
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.User, "after the marker")]);
        harness.Store.Save(harness.Paths, record);

        var log = harness.Store.LoadTranscriptLog(harness.Paths, harness.Load().Transcript);

        Assert.Equal(6, log.Entries.Count);
        Assert.Equal(5, log.AllMessages.Count);
        Assert.Equal(5, log.ActiveMessages.Count);
        Assert.Equal([0, -1, 2, 3, 5], log.ActiveLineIndices);
        Assert.Equal(ChatRole.System, log.ActiveMessages[0].Role);
        Assert.Equal(ChatRole.User, log.ActiveMessages[1].Role);
        Assert.Equal(CompactionAuthors.Summary, log.ActiveMessages[1].AuthorName);
        Assert.Equal("replacement text", log.ActiveMessages[1].GetText());
        Assert.Equal("before", log.ActiveMessages[2].GetText());
        Assert.Equal("tail before the marker", log.ActiveMessages[3].GetText());
        Assert.Equal("after the marker", log.ActiveMessages[4].GetText());
        Assert.Equal("Do the task.", log.OriginalTask);
        Assert.Equal(1, log.CompactionCount);
        Assert.Equal("Do the task.", log.AllMessages[1].GetText());
    }

    [Fact]
    public void AnEmptyKeptRangeKeepsNothing()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.User, "summary request")]);
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("replacement text", 2, 2));
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.User, "after the marker")]);
        harness.Store.Save(harness.Paths, record);

        var log = harness.Store.LoadTranscriptLog(harness.Paths, harness.Load().Transcript);

        Assert.Equal([0, -1, 4], log.ActiveLineIndices);
        Assert.Equal(3, log.ActiveMessages.Count);
        Assert.Equal("replacement text", log.ActiveMessages[1].GetText());
        Assert.Equal("after the marker", log.ActiveMessages[2].GetText());
    }

    [Fact]
    public void TwoMarkersUseTheLastOneAndKeepTheOriginalTask()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.Assistant, "first answer")]);
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("first replacement", 2, 3));
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.User, "middle")]);
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("second replacement", 4, 5));
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.User, "last")]);
        harness.Store.Save(harness.Paths, record);

        var log = harness.Store.LoadTranscriptLog(harness.Paths, harness.Load().Transcript);

        Assert.Equal(2, log.CompactionCount);
        Assert.Equal([0, -1, 4, 6], log.ActiveLineIndices);
        Assert.Equal(4, log.ActiveMessages.Count);
        Assert.Equal("second replacement", log.ActiveMessages[1].GetText());
        Assert.Equal("middle", log.ActiveMessages[2].GetText());
        Assert.Equal("last", log.ActiveMessages[3].GetText());
        Assert.Equal("Do the task.", log.OriginalTask);
        Assert.Contains(log.AllMessages, message => message.GetText() == "first answer");
        Assert.Contains(log.AllMessages, message => message.GetText() == "middle");
    }

    [Fact]
    public void AKeptRangePastItsEndFailsWithStateConflict()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("replacement", 2, 1));

        var exception = Assert.Throws<SubordinateException>(() => harness.Store.LoadTranscriptLog(harness.Paths, record.Transcript));

        Assert.Equal(ErrorCode.StateConflict, exception.Code);
    }

    [Fact]
    public void AKeptRangePastTheMarkerFailsWithStateConflict()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("replacement", 1, 3));

        var exception = Assert.Throws<SubordinateException>(() => harness.Store.LoadTranscriptLog(harness.Paths, record.Transcript));

        Assert.Equal(ErrorCode.StateConflict, exception.Code);
    }

    [Fact]
    public void AKeptRangeAtOrBeforeThePreviousMarkerFailsWithStateConflict()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("first replacement", 1, 1));
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.User, "middle")]);
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("second replacement", 2, 3));

        var exception = Assert.Throws<SubordinateException>(() => harness.Store.LoadTranscriptLog(harness.Paths, record.Transcript));

        Assert.Equal(ErrorCode.StateConflict, exception.Code);
    }

    [Fact]
    public void AKeptRangeThatIncludesAMarkerLineFailsWithStateConflict()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("first replacement", 1, 1));
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.Text(ChatRole.User, "middle")]);
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("second replacement", 1, 4));

        var exception = Assert.Throws<SubordinateException>(() => harness.Store.LoadTranscriptLog(harness.Paths, record.Transcript));

        Assert.Equal(ErrorCode.StateConflict, exception.Code);
    }

    [Fact]
    public void ThePointerCountsTheMarkerLine()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        var before = record.Transcript.Count;

        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("replacement", 1, 1));

        Assert.Equal(before + 1, record.Transcript.Count);
        Assert.Equal(before + 1, harness.Store.LoadTranscriptLog(harness.Paths, record.Transcript).Entries.Count);
    }

    [Fact]
    public void ATornMarkerAfterTheCommittedLengthIsIgnored()
    {
        using var harness = new SessionHarness();
        var committed = harness.Load().Transcript;
        File.AppendAllText(harness.Paths.TranscriptFile, "{\"kind\":\"compaction\",\"at\":\"2026-", Encoding.UTF8);

        var log = harness.Store.LoadTranscriptLog(harness.Paths, committed);

        Assert.Equal(0, log.CompactionCount);
        Assert.Equal(SessionHarness.Describe(harness.Transcript()), SessionHarness.Describe(log.ActiveMessages));
    }

    [Fact]
    public void ValidateAcceptsAPendingItemWhoseCallWasCompactedAway()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript,
        [
            SessionHarness.ShellCalls(("call_before", "ls")),
            new ChatMessage { Role = ChatRole.Tool, Contents = [new ToolResultContent { CallId = "call_before", ToolName = "shell", Output = "exit_code: 0" }] }
        ]);
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("compacted away", 3, 3));
        record.Pending.Add(new PendingRecord { Id = "abc", CallId = "call_before", Status = PendingStatuses.Approved, Delivered = false });
        record.Inflight = null;
        harness.Store.Save(harness.Paths, record);

        var active = SessionRunner.Validate(harness.Store, harness.Paths, harness.Load());

        Assert.DoesNotContain(active, message => message.GetToolCalls().Any(call => call.CallId == "call_before"));
        Assert.Contains("compacted away", active.Select(message => message.GetText()));
    }

    [Fact]
    public void AnInflightBatchAfterAMarkerValidatesAgainstTheActiveList()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.Transcript = harness.Store.AppendCompaction(harness.Paths, record.Transcript, Marker("compacted", 1, 2));
        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, [SessionHarness.ShellCalls(("call_after", "pwd"))]);
        var log = harness.Store.LoadTranscriptLog(harness.Paths, record.Transcript);
        var activeCount = log.ActiveMessages.Count;
        var allCount = log.AllMessages.Count;
        record.Inflight = new InflightRecord
        {
            AssistantIndex = activeCount - 1,
            Frames = [new InflightFrameRecord { CallId = "call_after", State = FrameStates.NotStarted }]
        };
        harness.Store.Save(harness.Paths, record);

        var active = SessionRunner.Validate(harness.Store, harness.Paths, harness.Load());

        Assert.Equal(activeCount, active.Count);
        Assert.NotEqual(allCount, activeCount);
        Assert.Equal("call_after", active[^1].GetToolCalls().Single().CallId);
    }

    [Fact]
    public void ARecordWithoutCompactionFieldsLoads()
    {
        var json = """
            {
              "schema": 1,
              "name": "old",
              "workdir": "work",
              "created_at": "2026-01-01T00:00:00+00:00",
              "updated_at": "2026-01-01T00:00:00+00:00",
              "shell": { "kind": "bash", "dialect": "bash", "executable_path": "bash.exe", "version": "5" },
              "model": { "alias": "test", "provider": "test", "model_id": "scripted" },
              "policy": [],
              "limits": { "max_turns": 20, "max_minutes": 5, "shell_timeout_seconds": 30 },
              "state": "finished",
              "transcript": { "count": 0, "bytes": 0 },
              "pending": [],
              "grants": [],
              "runs": [],
              "files_written": [],
              "counters": { "model_calls": 1, "tool_calls": 0, "input_tokens": 10, "cached_input_tokens": 0, "output_tokens": 5 },
              "context_tokens": 15
            }
            """;

        var record = JsonSerializer.Deserialize<SessionRecord>(json, SessionStore.RecordJsonOptions)!;

        Assert.Null(record.Compaction);
        Assert.Equal(0, record.Compactions);
        Assert.Equal(0, record.Counters.CompactionModelCalls);
        Assert.Equal(1, record.Counters.ModelCalls);
    }

    private static CompactionMarker Marker(string message, int keptFromLine, int keptToLine)
    {
        return new CompactionMarker
        {
            At = DateTimeOffset.UtcNow,
            RemovedMessages = 3,
            TokensBefore = 123456,
            Message = message,
            KeptFromLine = keptFromLine,
            KeptToLine = keptToLine
        };
    }
}

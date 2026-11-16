using System.Text;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core;
using Subordinate.Core.Gate;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class SessionStoreTests
{
    [WindowsFact]
    public void SessionIdentityIsStable()
    {
        var home = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
        var store = new SessionStore(home);

        Assert.Equal("my-session-08c207a26afc", Path.GetFileName(store.PathsFor(@"D:\Work\Repo", "My Session").Directory));
    }

    [Fact]
    public void DuplicateStartForTheSameWorkdirAndNameFails()
    {
        using var harness = new SessionHarness();
        var duplicate = new SessionRecord { Name = "test", Workdir = harness.Workdir };

        var exception = Assert.Throws<SubordinateException>(() => harness.Store.Create(duplicate));

        Assert.Equal(ErrorCode.SessionExists, exception.Code);
    }

    [Fact]
    public void SameNameInAnotherWorkdirIsADifferentSession()
    {
        using var harness = new SessionHarness();
        var otherWorkdir = Path.Combine(Path.GetDirectoryName(harness.Workdir)!, "other");

        var paths = harness.Store.PathsFor(otherWorkdir, "test");

        Assert.NotEqual(harness.Paths.Directory, paths.Directory);
        Assert.Equal(harness.Paths.Directory, harness.Store.PathsFor(harness.Workdir.ToUpperInvariant(), "test").Directory);
    }

    private static readonly string Accented = "caf" + (char)0x00E9;
    private static readonly string EmDash = ((char)0x2014).ToString();
    private static readonly string AlmostEqual = ((char)0x2248).ToString();

    [Theory]
    [InlineData("")]
    [InlineData("has\ttab")]
    public void RejectsUnusableNames(string name)
    {
        var exception = Assert.Throws<SubordinateException>(() => SessionStore.ValidateName(name));

        Assert.Equal(ErrorCode.InvalidArguments, exception.Code);
    }

    [Fact]
    public void RejectsNonAsciiNames()
    {
        var exception = Assert.Throws<SubordinateException>(() => SessionStore.ValidateName(Accented));

        Assert.Equal(ErrorCode.InvalidArguments, exception.Code);
    }

    [Fact]
    public void TranscriptRoundTripsEveryContentType()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        var messages = new List<ChatMessage>
        {
            SessionHarness.ShellCalls(("call_1", $"echo \"{Accented}\" {EmDash} done")),
            new() { Role = ChatRole.Tool, Contents = [new ToolResultContent { CallId = "call_1", ToolName = "shell", Output = $"line1\nline2 {AlmostEqual}", IsError = true }] },
            new() { Role = ChatRole.Assistant, Contents = [new ReasoningContent { Text = "thinking", Signature = "sig" }, new TextContent { Text = "final" }] }
        };

        record.Transcript = harness.Store.AppendTranscript(harness.Paths, record.Transcript, messages);
        harness.Store.Save(harness.Paths, record);

        var loaded = harness.Transcript();

        Assert.Equal(SessionHarness.Describe([.. harness.Transcript().Take(2), .. messages]), SessionHarness.Describe(loaded));
        var reasoning = Assert.IsType<ReasoningContent>(loaded[^1].Contents[0]);
        Assert.Equal("sig", reasoning.Signature);
    }

    [Fact]
    public void TornTranscriptTailIsIgnoredAndOverwritten()
    {
        using var harness = new SessionHarness();
        var committed = harness.Load().Transcript;

        File.AppendAllText(harness.Paths.TranscriptFile, "{\"role\":\"assistant\",\"conte", Encoding.UTF8);

        Assert.Equal(committed.Count, harness.Transcript().Count);

        var pointer = harness.Store.AppendTranscript(harness.Paths, committed, [SessionHarness.Text(ChatRole.Assistant, "after")]);
        var loaded = harness.Store.LoadTranscript(harness.Paths, pointer);

        Assert.Equal(committed.Count + 1, loaded.Count);
        Assert.Equal("after", loaded[^1].GetText());
    }

    [Fact]
    public void TruncatedTranscriptIsAStateConflict()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        File.WriteAllBytes(harness.Paths.TranscriptFile, []);

        var exception = Assert.Throws<SubordinateException>(() => harness.Store.LoadTranscript(harness.Paths, record.Transcript));

        Assert.Equal(ErrorCode.StateConflict, exception.Code);
    }

    [Fact]
    public void PendingIdsAreDerivedFromTheCallIdAndSurviveCollisions()
    {
        var record = new SessionRecord { Policy = ["read"] };
        var ledger = new PendingLedger(record);

        Assert.Equal(PendingLedger.DeriveId("call_x"), PendingLedger.DeriveId("call_x"));
        Assert.NotEqual(PendingLedger.DeriveId("call_x"), PendingLedger.DeriveId("call_y"));
        Assert.Matches("^[0-9a-f]{10}$", PendingLedger.DeriveId("call_x"));

        record.Pending.Add(new PendingRecord { Id = PendingLedger.DeriveId("call_x"), CallId = "another_call" });

        var allocated = ledger.AllocateId("call_x");

        Assert.NotEqual(PendingLedger.DeriveId("call_x"), allocated);
        Assert.Equal(allocated, ledger.AllocateId("call_x"));
    }
}

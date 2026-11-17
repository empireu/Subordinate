using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Output;
using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;

namespace Subordinate.Tests;

public class SessionCatalogTests
{
    [Fact]
    public void ReportsStatesAndCounts()
    {
        using var harness = new CatalogHarness();
        harness.CreateSession("finished", SessionStates.Finished);
        harness.CreateSession("approval", SessionStates.AwaitingApproval, record =>
        {
            record.Pending.Add(new PendingRecord { Id = "1", CallId = "call_1", Tool = "shell", Summary = "rm build.log", NeedsHuman = true, Status = PendingStatuses.Pending });
            record.Pending.Add(new PendingRecord { Id = "2", CallId = "call_2", Tool = "shell", Summary = "ls", Status = PendingStatuses.Pending });
        });
        harness.CreateRunningSession("fresh", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-60));
        harness.CreateRunningSession("stale", DateTimeOffset.UtcNow.AddSeconds(-60), DateTimeOffset.UtcNow.AddSeconds(-60));
        harness.CreateCorruptSession("corrupt");

        var catalog = new SessionCatalog(harness.Home);
        catalog.PollOnce();
        var snapshot = catalog.Snapshot;

        Assert.Equal(4, snapshot.Sessions.Count);
        Assert.Equal(1, snapshot.UnreadableCount);
        Assert.Equal("finished", snapshot.Sessions.Single(session => session.Name == "finished").DisplayState);
        var approval = snapshot.Sessions.Single(session => session.Name == "approval");
        Assert.Equal("approval", approval.DisplayState);
        Assert.Equal(2, approval.PendingCount);
        Assert.Equal(1, approval.NeedsHumanCount);
        Assert.Equal("running", snapshot.Sessions.Single(session => session.Name == "fresh").DisplayState);
        Assert.Equal("interrupted", snapshot.Sessions.Single(session => session.Name == "stale").DisplayState);
    }

    [Fact]
    public void UnchangedFilesAreNotReRead()
    {
        using var harness = new CatalogHarness();
        var first = harness.CreateSession("one", SessionStates.Finished);
        harness.CreateSession("two", SessionStates.Finished);
        var catalog = new SessionCatalog(harness.Home);

        catalog.PollOnce();
        Assert.Equal(2, catalog.RecordReadCount);
        catalog.PollOnce();
        Assert.Equal(2, catalog.RecordReadCount);

        var record = harness.Store.Load(first);
        record.FinalText = "changed";
        harness.Store.Save(first, record);
        catalog.PollOnce();

        Assert.Equal(3, catalog.RecordReadCount);
    }

    [Fact]
    public void TheCatalogNeverWrites()
    {
        using var harness = new CatalogHarness();
        harness.CreateSession("one", SessionStates.Finished);
        harness.CreateSession("two", SessionStates.AwaitingApproval);
        var catalog = new SessionCatalog(harness.Home);
        var before = SnapshotFiles(harness.Home);

        catalog.PollOnce();
        catalog.PollOnce();
        catalog.PollOnce();

        Assert.Equal(before, SnapshotFiles(harness.Home));
        Assert.False(File.Exists(Path.Combine(harness.Home, "sessions", "one", "session.lock")));
    }

    [Fact]
    public void ADeletedSessionDisappears()
    {
        using var harness = new CatalogHarness();
        var first = harness.CreateSession("one", SessionStates.Finished);
        harness.CreateSession("two", SessionStates.Finished);
        var catalog = new SessionCatalog(harness.Home);
        catalog.PollOnce();
        Assert.Equal(2, catalog.Snapshot.Sessions.Count);

        Directory.Delete(first.Directory, true);
        catalog.PollOnce();

        Assert.Single(catalog.Snapshot.Sessions);
    }

    private static List<string> SnapshotFiles(string home)
    {
        return Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc:O}";
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class CatalogHarness : IDisposable
    {
        public CatalogHarness()
        {
            Root = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
            Home = Path.Combine(Root, "home");
            Workdir = Path.Combine(Root, "work");
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(Workdir);
            Store = new SessionStore(Home);
        }

        public string Root { get; }

        public string Home { get; }

        public string Workdir { get; }

        public SessionStore Store { get; }

        public SessionPaths CreateSession(string name, string state, Action<SessionRecord>? configure = null)
        {
            var record = new SessionRecord
            {
                Name = name,
                Workdir = Workdir,
                CreatedAt = DateTimeOffset.UtcNow,
                Shell = new ShellRecord { Kind = "bash", Dialect = "bash", ExecutablePath = "bash.exe", Version = "5.2.26" },
                Model = new ModelRecord { Alias = "test", Provider = "test", ModelId = "scripted" },
                Policy = ["readonly"],
                Limits = new LimitsRecord { MaxTurns = 20, MaxMinutes = 5, ShellTimeoutSeconds = 30 },
                State = state
            };
            configure?.Invoke(record);
            var paths = Store.Create(record);
            record.Transcript = Store.AppendTranscript(paths, record.Transcript,
            [
                new ChatMessage { Role = ChatRole.System, Contents = [new TextContent { Text = "You are a test subagent." }] },
                new ChatMessage { Role = ChatRole.User, Contents = [new TextContent { Text = "Do the task." }] }
            ]);
            Store.Save(paths, record);
            return paths;
        }

        public void CreateRunningSession(string name, DateTimeOffset statusUpdatedAt, DateTimeOffset recordUpdatedAt)
        {
            var paths = CreateSession(name, SessionStates.Running);
            var record = Store.Load(paths);
            var runId = Store.CreateRunDirectory(paths, 1, 10);
            record.Runs.Add(new RunRecord { RunId = runId, Kind = "start", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), State = SessionStates.Running });
            Store.Save(paths, record);
            record = Store.Load(paths);
            var run = record.CurrentRun!;
            var runDirectory = paths.RunDirectory(run.RunId);
            RunArtifacts.WriteStatus(runDirectory, new StatusSnapshot
            {
                Session = record.Name,
                Workdir = record.Workdir,
                RunId = run.RunId,
                State = SessionStates.Running,
                ProcessId = 1234,
                StartedAt = run.StartedAt,
                ElapsedSeconds = 12.5,
                PendingCount = 0,
                RunCounters = run.Counters,
                SessionCounters = record.Counters,
                TurnsRemaining = 5,
                LastActivity = "waiting for the model",
                ContextTokens = 1000
            });

            var statusPath = RunArtifacts.StatusFile(runDirectory);
            var status = JsonNode.Parse(File.ReadAllText(statusPath))!.AsObject();
            status["updated_at"] = RunArtifacts.FormatTime(statusUpdatedAt);
            File.WriteAllText(statusPath, status.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            record.UpdatedAt = recordUpdatedAt;
            File.WriteAllText(paths.RecordFile, JsonSerializer.Serialize(record, SessionStore.RecordJsonOptions));
        }

        public void CreateCorruptSession(string name)
        {
            var directory = Path.Combine(Home, "sessions", name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "session.json"), "not json");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

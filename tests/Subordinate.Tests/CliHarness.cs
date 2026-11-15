using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Configuration;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

internal sealed class CliHarness : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public CliHarness()
    {
        _root = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
        Home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Home);
        _previousHome = Environment.GetEnvironmentVariable("SUBORDINATE_HOME");
        Environment.SetEnvironmentVariable("SUBORDINATE_HOME", Home);
    }

    public string Home { get; }

    public string CreateWorkdir(string name = "work")
    {
        var workdir = Path.Combine(_root, name);
        Directory.CreateDirectory(workdir);
        return workdir;
    }

    public SessionRecord CreateSession(SessionStore store, string name, string workdir, string state, CountersRecord? counters = null)
    {
        var record = new SessionRecord
        {
            Name = name,
            Workdir = workdir,
            CreatedAt = DateTimeOffset.UtcNow,
            Shell = new ShellRecord { Kind = "bash", Dialect = "bash", ExecutablePath = "bash.exe", Version = "5.2.26" },
            Model = new ModelRecord { Alias = "test", Provider = "test", ModelId = "scripted" },
            Policy = ["readonly"],
            Limits = new LimitsRecord { MaxTurns = 20, MaxMinutes = 5, ShellTimeoutSeconds = 30 },
            State = state,
            Counters = counters ?? new CountersRecord()
        };
        var paths = store.Create(record);
        record.Transcript = store.AppendTranscript(paths, record.Transcript,
        [
            new ChatMessage { Role = ChatRole.System, Contents = [new TextContent { Text = "You are a test subagent." }] },
            new ChatMessage { Role = ChatRole.User, Contents = [new TextContent { Text = "Do the task." }] }
        ]);
        store.Save(paths, record);
        return record;
    }

    public void WriteConfig(string json)
    {
        File.WriteAllText(Path.Combine(Home, SubordinateConfig.FileName), json);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SUBORDINATE_HOME", _previousHome);

        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

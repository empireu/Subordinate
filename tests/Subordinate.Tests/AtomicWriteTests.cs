using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class AtomicWriteTests
{
    [Fact]
    public void Save_succeeds_while_a_shared_reader_holds_the_record()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.FinalText = "updated";

        using (var reader = new FileStream(harness.Paths.RecordFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            harness.Store.Save(harness.Paths, record);
        }

        Assert.Equal("updated", harness.Load().FinalText);
    }

    [WindowsFact]
    public async Task Save_retries_until_a_blocking_reader_closes()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.FinalText = "new value";
        var reader = new FileStream(harness.Paths.RecordFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var save = Task.Run(() => harness.Store.Save(harness.Paths, record));

        await Task.Delay(100);
        reader.Dispose();
        await save.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("new value", harness.Load().FinalText);
    }

    [WindowsFact]
    public async Task Save_gives_up_after_the_retry_budget()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        record.FinalText = "new value";
        using var reader = new FileStream(harness.Paths.RecordFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var save = Task.Run(() => harness.Store.Save(harness.Paths, record));
        var completed = await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(save, completed);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => save);
        Assert.True(exception is IOException or UnauthorizedAccessException, exception.GetType().Name);
        Assert.NotEqual("new value", harness.Load().FinalText);
    }

    [Fact]
    public async Task Load_never_sees_a_partial_record()
    {
        using var harness = new SessionHarness();
        var record = harness.Load();
        var corruptRecords = new List<SubordinateException>();
        var successes = 0;

        var saver = Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                record.FinalText = "value " + index;
                harness.Store.Save(harness.Paths, record);
            }
        });

        var loader = Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                try
                {
                    harness.Store.Load(harness.Paths);
                    Interlocked.Increment(ref successes);
                }
                catch (SubordinateException ex) when (ex.Code == ErrorCode.UnknownSession)
                {
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                catch (SubordinateException ex)
                {
                    lock (corruptRecords)
                    {
                        corruptRecords.Add(ex);
                    }
                }
            }
        });

        await Task.WhenAll(saver, loader).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Empty(corruptRecords);
        Assert.True(successes >= 100, $"Only {successes} of 200 loads succeeded.");
    }

    [Fact]
    public async Task Run_finishes_while_a_reader_polls_the_record()
    {
        using var harness = new SessionHarness();
        using var cancellation = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                ReadShared(harness.Paths.RecordFile);

                if (Directory.Exists(harness.Paths.RunsDirectory))
                {
                    foreach (var statusFile in Directory.EnumerateFiles(harness.Paths.RunsDirectory, "status.json", SearchOption.AllDirectories))
                    {
                        ReadShared(statusFile);
                    }
                }

                await Task.Delay(5);
            }
        });

        var provider = new BlockingProvider(SessionHarness.ShellCalls(("call_1", "ls")), SessionHarness.Text(ChatRole.Assistant, "done"));
        var run = harness.RunAsync(provider);
        await provider.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        provider.Release.TrySetResult();
        var (outcome, _) = await run.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionStates.Finished, outcome.State);
    }

    private static void ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

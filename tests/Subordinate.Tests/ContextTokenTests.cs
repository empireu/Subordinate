using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Configuration;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class ContextTokenTests
{
    [Fact]
    public async Task OneCallRecordsContextTokensAndStatus()
    {
        using var harness = new SessionHarness();
        var provider = new UsageSequenceProvider(
            (SessionHarness.Text(ChatRole.Assistant, "done"), new ChatUsage { InputTokens = 1000, OutputTokens = 50 }));

        var (outcome, record) = await harness.RunAsync(provider);

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(1050, record.ContextTokens);
        var status = ReadStatus(harness, record);
        Assert.Equal(1050, status.GetProperty("context_tokens").GetInt64());
    }

    [Fact]
    public async Task TwoCallsLastUsageWins()
    {
        using var harness = new SessionHarness();
        var provider = new UsageSequenceProvider(
            (SessionHarness.ShellCalls(("call_1", "ls")), new ChatUsage { InputTokens = 100, OutputTokens = 10 }),
            (SessionHarness.Text(ChatRole.Assistant, "done"), new ChatUsage { InputTokens = 200, OutputTokens = 20 }));

        var (outcome, record) = await harness.RunAsync(provider);

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(220, record.ContextTokens);
    }

    [Fact]
    public async Task NoUsageLeavesContextAtZero()
    {
        using var harness = new SessionHarness();
        var provider = new ScriptedProvider(SessionHarness.Text(ChatRole.Assistant, "done"));

        var (outcome, record) = await harness.RunAsync(provider);

        Assert.Equal(SessionStates.Finished, outcome.State);
        Assert.Equal(0, record.ContextTokens);
    }

    [Fact]
    public void OldRecordWithoutContextTokensLoadsAsZero()
    {
        using var harness = new SessionHarness();
        var node = JsonNode.Parse(File.ReadAllText(harness.Paths.RecordFile))!.AsObject();
        node.Remove("context_tokens");
        File.WriteAllText(harness.Paths.RecordFile, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var record = harness.Store.Load(harness.Paths);

        Assert.Equal(0, record.ContextTokens);
    }

    [Fact]
    public void ConfigParsesContextWindow()
    {
        var home = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        try
        {
            File.WriteAllText(Path.Combine(home, SubordinateConfig.FileName), """
                {
                  "providers": {
                    "test": { "endpoint": "https://example.invalid/v1", "api_key": "test" }
                  },
                  "models": {
                    "big": { "provider": "test", "model": "big-model", "context_window": 128000 }
                  },
                  "default_model": "big"
                }
                """);

            var config = SubordinateConfig.Load(home);

            Assert.Equal(128000, config.Models["big"].ContextWindow);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public async Task StatusKeysKeepTheirOrderWithContextTokensLast()
    {
        using var harness = new SessionHarness();
        var provider = new UsageSequenceProvider(
            (SessionHarness.Text(ChatRole.Assistant, "done"), new ChatUsage { InputTokens = 1000, OutputTokens = 50 }));

        var (_, record) = await harness.RunAsync(provider);
        var status = ReadStatus(harness, record);
        var names = status.EnumerateObject().Select(property => property.Name).ToList();

        Assert.Equal(
            ["schema", "session", "workdir", "run_id", "state", "pid", "started_at", "updated_at", "elapsed_seconds", "run", "session_totals", "turns_remaining", "pending_count", "last_activity", "context_tokens"],
            names);
    }

    private static JsonElement ReadStatus(SessionHarness harness, SessionRecord record)
    {
        var path = Path.Combine(harness.Paths.RunDirectory(record.CurrentRun!.RunId), "status.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private sealed class UsageSequenceProvider(params (ChatMessage Response, ChatUsage? Usage)[] responses) : IChatProvider
    {
        private readonly Queue<(ChatMessage Response, ChatUsage? Usage)> _responses = new(responses);

        public string ProviderId => "usage-sequence";

        public string? DefaultModel => "usage-sequence";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (!_responses.TryDequeue(out var response))
            {
                yield return new StreamErrorEvent(new ChatError { Message = "The usage sequence provider has no more responses." });
                yield break;
            }

            if (response.Usage != null)
            {
                yield return new UsageReceivedEvent(response.Usage);
            }

            var finishReason = response.Response.Contents.OfType<ToolCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
            yield return new StreamCompletedEvent(finishReason, response.Response);
        }

        public async Task<ChatMessage> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            await foreach (var streamEvent in StreamAsync(request, cancellationToken))
            {
                if (streamEvent is StreamCompletedEvent completed)
                {
                    return completed.Message;
                }
            }

            throw new InvalidOperationException("The usage sequence provider has no completed message.");
        }
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Classification;
using Subordinate.Core.Monitoring;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;
using Subordinate.Core.Shells;

namespace Subordinate.Tests;

/// <summary>
///     A session on disk in a temporary directory, run with a scripted model and a fake shell.
/// </summary>
internal sealed class SessionHarness : IDisposable
{
    private readonly string _root;

    public SessionHarness(string policy = "readonly")
    {
        _root = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
        Workdir = Path.Combine(_root, "work");
        Directory.CreateDirectory(Workdir);
        Store = new SessionStore(Path.Combine(_root, "home"));

        var record = new SessionRecord
        {
            Name = "test",
            Workdir = Workdir,
            CreatedAt = DateTimeOffset.UtcNow,
            Shell = new ShellRecord { Kind = ShellKind.Bash.ToToken(), Dialect = ShellKind.Bash.DialectName(), ExecutablePath = "bash.exe", Version = "5.2.26" },
            Model = new ModelRecord { Alias = "test", Provider = "test", ModelId = "scripted" },
            Policy = [.. policy.Split(',')],
            Limits = new LimitsRecord { MaxTurns = 20, MaxMinutes = 5, ShellTimeoutSeconds = 30 },
            State = SessionStates.Starting
        };

        Paths = Store.Create(record);
        record.Transcript = Store.AppendTranscript(Paths, record.Transcript,
        [
            Text(ChatRole.System, "You are a test subagent."),
            Text(ChatRole.User, "Do the task.")
        ]);
        Store.Save(Paths, record);
    }

    public string Workdir { get; }

    public SessionStore Store { get; }

    public SessionPaths Paths { get; }

    public FakeExecutor Executor { get; } = new();

    public SessionRecord Load()
    {
        return Store.Load(Paths);
    }

    public void Update(Action<SessionRecord> change)
    {
        var record = Load();
        change(record);
        Store.Save(Paths, record);
    }

    public async Task<(RunOutcome Outcome, SessionRecord Record)> RunAsync(IChatProvider provider, RunLimits? limits = null, ICommandExecutor? executor = null, CancellationToken cancellationToken = default, IMonitorFeed? monitorFeed = null, CompactionSettings? compaction = null)
    {
        var record = Load();
        var runId = Store.CreateRunDirectory(Paths, record.Runs.Count + 1, 10);
        record.Runs.Add(new RunRecord { RunId = runId, Kind = record.Runs.Count == 0 ? "start" : "resume", StartedAt = DateTimeOffset.UtcNow, State = SessionStates.Starting });
        record.State = SessionStates.Starting;
        Store.Save(Paths, record);

        var runner = new SessionRunner(Store, Paths, record, runId, provider, new ChatRequestOptions(), executor ?? Executor, monitorFeed, compaction);
        var outcome = await runner.RunAsync(limits ?? new RunLimits { MaxModelCalls = 10, MaxDuration = TimeSpan.FromMinutes(1) }, cancellationToken);
        return (outcome, Load());
    }

    public List<ChatMessage> Transcript()
    {
        var record = Load();
        return Store.LoadTranscript(Paths, record.Transcript);
    }

    public static ChatMessage Text(ChatRole role, string text)
    {
        return new ChatMessage { Role = role, Contents = [new TextContent { Text = text }] };
    }

    public static ChatMessage ShellCalls(params (string CallId, string Command)[] calls)
    {
        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Contents = calls.Select(call => (ChatContent)new ToolCallContent
            {
                CallId = call.CallId,
                ToolName = "shell",
                Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["command"] = call.Command })
            }).ToList()
        };
    }

    public static IEnumerable<ToolResultContent> ToolResults(IEnumerable<ChatMessage> messages)
    {
        return messages.SelectMany(message => message.Contents.OfType<ToolResultContent>());
    }

    public static string Describe(IEnumerable<ChatMessage> messages)
    {
        return string.Join("\n", messages.Select(message => $"{message.Role}: " + string.Join(" | ", message.Contents.Select(content => content switch
        {
            TextContent text => "text " + text.Text,
            ToolCallContent call => $"call {call.CallId} {call.ToolName} {call.Arguments.GetRawText()}",
            ToolResultContent result => $"result {result.CallId} error={result.IsError} {result.Output}",
            _ => content.GetType().Name
        }))));
    }

    public void Dispose()
    {
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

/// <summary>
///     Returns queued assistant messages in order and records every request it receives.
/// </summary>
internal sealed class ScriptedProvider(params ChatMessage[] responses) : IChatProvider
{
    private readonly Queue<ChatMessage> _responses = new(responses);

    public List<List<ChatMessage>> Requests { get; } = [];

    public List<ChatRequest> FullRequests { get; } = [];

    public ChatUsage? Usage { get; set; }

    public Queue<ChatUsage?>? Usages { get; set; }

    public Queue<ChatFinishReason>? FinishReasons { get; set; }

    public string ProviderId => "scripted";

    public string? DefaultModel => "scripted";

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        Requests.Add(request.Messages.ToList());
        FullRequests.Add(request);

        if (!_responses.TryDequeue(out var response))
        {
            yield return new StreamErrorEvent(new ChatError { Message = "The scripted provider has no more responses." });
            yield break;
        }

        var usage = Usages is { Count: > 0 } ? Usages.Dequeue() : Usage;

        if (usage != null)
        {
            yield return new UsageReceivedEvent(usage);
        }

        var finishReason = FinishReasons is { Count: > 0 }
            ? FinishReasons.Dequeue()
            : response.Contents.OfType<ToolCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
        yield return new StreamCompletedEvent(finishReason, response);
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

        throw new InvalidOperationException("The scripted provider has no more responses.");
    }
}

/// <summary>
///     Records commands instead of running them and answers each with a fixed output.
/// </summary>
internal sealed class FakeExecutor : ICommandExecutor
{
    public List<string> Commands { get; } = [];

    public ShellKind Shell => ShellKind.Bash;

    public Task<CommandExecutionResult> ExecuteAsync(CommandExecutionRequest request, CancellationToken cancellationToken)
    {
        Commands.Add(request.Command);
        var output = $"ran: {request.Command}";

        return Task.FromResult(new CommandExecutionResult
        {
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(5),
            StandardOutput = new CapturedOutput(output, output.Length, false, null),
            StandardError = new CapturedOutput(string.Empty, 0, false, null)
        });
    }
}

internal sealed class BlockingProvider(ChatMessage first, ChatMessage second) : IChatProvider
{
    public TaskCompletionSource SecondRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<ChatRequest> Requests { get; } = [];

    public string ProviderId => "blocking";

    public string? DefaultModel => "blocking";

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var index = Requests.Count;
        Requests.Add(request);

        ChatMessage response;

        if (index == 0)
        {
            response = first;
        }
        else
        {
            SecondRequestStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            response = second;
        }

        var finishReason = response.Contents.OfType<ToolCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
        yield return new StreamCompletedEvent(finishReason, response);
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

        throw new InvalidOperationException("The blocking provider has no response.");
    }
}

internal sealed class CancellationErrorProvider(ChatMessage first, ChatMessage second) : IChatProvider
{
    public TaskCompletionSource SecondRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<ChatRequest> Requests { get; } = [];

    public string ProviderId => "cancellation-error";

    public string? DefaultModel => "cancellation-error";

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var index = Requests.Count;
        Requests.Add(request);

        if (index == 0)
        {
            yield return new StreamCompletedEvent(GetFinishReason(first), first);
            yield break;
        }

        SecondRequestStarted.TrySetResult();
        var cancelled = false;

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        if (cancelled)
        {
            yield return new StreamErrorEvent(new ChatError { Message = "The request was cancelled.", Code = "cancelled", IsRetryable = false });
            yield break;
        }

        yield return new StreamCompletedEvent(GetFinishReason(second), second);
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

        throw new InvalidOperationException("The cancellation provider has no completed message.");
    }

    private static ChatFinishReason GetFinishReason(ChatMessage message)
    {
        return message.Contents.OfType<ToolCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
    }
}

internal sealed class ThrowingCancellationProvider(ChatMessage first, ChatMessage second) : IChatProvider
{
    public TaskCompletionSource SecondRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<ChatRequest> Requests { get; } = [];

    public string ProviderId => "throwing-cancellation";

    public string? DefaultModel => "throwing-cancellation";

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var index = Requests.Count;
        Requests.Add(request);

        if (index == 0)
        {
            yield return new StreamCompletedEvent(GetFinishReason(first), first);
            yield break;
        }

        SecondRequestStarted.TrySetResult();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new ChatProviderException(new ChatError { Message = "The request was cancelled.", Code = "cancelled", IsRetryable = false });
        }

        yield return new StreamCompletedEvent(GetFinishReason(second), second);
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

        throw new InvalidOperationException("The throwing cancellation provider has no completed message.");
    }

    private static ChatFinishReason GetFinishReason(ChatMessage message)
    {
        return message.Contents.OfType<ToolCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
    }
}

internal sealed class BlockingExecutor : ICommandExecutor
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Commands { get; } = [];

    public ShellKind Shell => ShellKind.Bash;

    public async Task<CommandExecutionResult> ExecuteAsync(CommandExecutionRequest request, CancellationToken cancellationToken)
    {
        Commands.Add(request.Command);
        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        var output = $"ran: {request.Command}";

        return new CommandExecutionResult
        {
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(5),
            StandardOutput = new CapturedOutput(output, output.Length, false, null),
            StandardError = new CapturedOutput(string.Empty, 0, false, null)
        };
    }
}

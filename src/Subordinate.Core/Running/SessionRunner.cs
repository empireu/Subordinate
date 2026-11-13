using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KnowledgeSystem.Agents.Orchestration;
using KnowledgeSystem.Agents.Orchestration.Tools;
using KnowledgeSystem.Agents.Tools;
using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Events.Implementation;
using Subordinate.Core.Classification;
using Subordinate.Core.Gate;
using Subordinate.Core.Output;
using Subordinate.Core.Sessions;
using Subordinate.Core.Shells;

namespace Subordinate.Core.Running;

public sealed class RunLimits
{
    public required int MaxModelCalls { get; init; }

    public required TimeSpan MaxDuration { get; init; }
}

public sealed class RunOutcome
{
    public required string State { get; init; }

    public required string RunId { get; init; }

    public required string RunDirectory { get; init; }

    public ErrorRecord? Error { get; init; }
}

/// <summary>
///     Drives one run of a session: restores the conversation, delivers decisions, steps the agent and persists after every step.
/// </summary>
public sealed class SessionRunner
{
    private const string PendingPrefix = "PENDING APPROVAL ";
    private const int RecordedOutputLimit = 4000;

    private readonly SessionStore _store;
    private readonly SessionPaths _paths;
    private readonly SessionRecord _record;
    private readonly string _runId;
    private readonly string _runDirectory;
    private readonly IChatProvider _provider;
    private readonly ChatRequestOptions _requestOptions;
    private readonly ICommandExecutor _executor;
    private readonly HashSet<string> _startedCallIds = [];
    private readonly Stopwatch _stopwatch = new();

    private CountingChatProvider? _counting;
    private AgentRunner<SubordinateContext>? _runner;
    private SubordinateContext? _context;
    private PendingLedger? _ledger;
    private bool _persistenceEnabled;
    private int _persistedCount;
    private int _countedModelCalls;
    private long _countedInputTokens;
    private long _countedCachedInputTokens;
    private long _countedOutputTokens;
    private string _lastActivity = "starting";

    public SessionRunner(SessionStore store, SessionPaths paths, SessionRecord record, string runId, IChatProvider provider, ChatRequestOptions requestOptions, ICommandExecutor executor)
    {
        _store = store;
        _paths = paths;
        _record = record;
        _runId = runId;
        _runDirectory = paths.RunDirectory(runId);
        _provider = provider;
        _requestOptions = new ChatRequestOptions
        {
            Model = requestOptions.Model,
            Temperature = requestOptions.Temperature,
            MaxOutputTokens = requestOptions.MaxOutputTokens,
            ReasoningEffort = requestOptions.ReasoningEffort,
            ToolChoice = requestOptions.ToolChoice,
            PromptCacheKey = requestOptions.PromptCacheKey ?? CreatePromptCacheKey(record)
        };
        _executor = executor;
    }

    private static string CreatePromptCacheKey(SessionRecord record)
    {
        var identity = $"{record.Name}|{record.Workdir}|{record.CreatedAt.UtcTicks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "subordinate-" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    public string RunDirectory => _runDirectory;

    public async Task<RunOutcome> RunAsync(RunLimits limits, CancellationToken cancellationToken)
    {
        _stopwatch.Start();
        Directory.CreateDirectory(_runDirectory);
        TryDelete(_paths.StopFile);

        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var pollerSource = new CancellationTokenSource();
        var poller = PollStopRequestAsync(stopSource, pollerSource.Token);
        string state;
        ErrorRecord? error = null;

        try
        {
            state = await RunCoreAsync(limits, stopSource.Token);
        }
        catch (SubordinateException ex)
        {
            state = SessionStates.Failed;
            error = new ErrorRecord { Code = ex.Code.ToToken(), Message = ex.Message };
        }
        catch (OperationCanceledException)
        {
            state = SessionStates.Stopped;
        }
        catch (Exception ex)
        {
            state = SessionStates.Failed;
            error = new ErrorRecord { Code = ErrorCode.InternalError.ToToken(), Message = $"The run failed unexpectedly: {ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            await pollerSource.CancelAsync();

            try
            {
                await poller;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (error == null && state == SessionStates.Failed && _runner?.FinishError != null)
        {
            error = new ErrorRecord { Code = "agent_error", Message = _runner.FinishError.Message };
        }

        Finish(state, error);
        return new RunOutcome { State = state, RunId = _runId, RunDirectory = _runDirectory, Error = error };
    }

    private async Task<string> RunCoreAsync(RunLimits limits, CancellationToken cancellationToken)
    {
        var shell = ParseShell(_record.Shell.Kind);
        var environment = new ToolEnvironment
        {
            Workdir = _record.Workdir,
            Shell = shell,
            Executor = _executor,
            OutputDirectory = _runDirectory,
            ReadableRoots = [_paths.RunsDirectory],
            DefaultShellTimeoutSeconds = _record.Limits.ShellTimeoutSeconds > 0 ? _record.Limits.ShellTimeoutSeconds : 120
        };

        environment.FilesWritten.UnionWith(_record.FilesWritten);
        _context = new SubordinateContext(environment);

        var transcript = Validate(_store, _paths, _record);
        foreach (var message in transcript)
        {
            _context.Timeline.InsertChat(message);
        }

        _persistedCount = transcript.Count;

        var ledger = new PendingLedger(_record);
        _ledger = ledger;
        var gate = new SubordinateGate(ledger, () => _runId, MarkExecutionStartingAsync);
        var agent = new SubordinateAgent(shell, gate);
        _counting = new CountingChatProvider(_provider);
        _runner = new AgentRunner<SubordinateContext>(
            _counting,
            agent,
            null,
            _context,
            NullEventManager.Instance,
            cancellationToken,
            AgentRunner.ICompletionFactory.Wrap(() => _requestOptions));

        var inflightCallIds = new HashSet<string>();

        if (_record.Inflight != null)
        {
            inflightCallIds = await RestoreInflightAsync(transcript, ledger);
        }

        _persistenceEnabled = true;
        var updates = await DeliverDecisionsAsync(agent, gate, ledger, inflightCallIds, cancellationToken);
        var updateInserted = false;
        var modelCallsAtStart = _counting.ModelCalls;

        _record.State = SessionStates.Running;
        Persist();

        while (true)
        {
            if (!updateInserted && _runner.ActiveToolCalls.Count == 0)
            {
                updateInserted = true;

                if (updates != null)
                {
                    _context.InsertUserMessage(updates);
                    MarkDelivered(ledger, inflightCallIds);
                    Persist();
                }
            }

            if (_runner.IsFinished)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return SessionStates.Stopped;
            }

            if (_stopwatch.Elapsed >= limits.MaxDuration)
            {
                _lastActivity = "time limit reached";
                return SessionStates.LimitReached;
            }

            if (_runner.ActiveToolCalls.Count == 0 && _counting.ModelCalls - modelCallsAtStart >= limits.MaxModelCalls)
            {
                _lastActivity = "turn limit reached";
                return SessionStates.LimitReached;
            }

            if (_runner.ActiveToolCalls.Count == 0)
            {
                _lastActivity = "waiting for the model";
                WriteStatus(SessionStates.Running);
            }

            try
            {
                await _runner.ExecuteTurn();
            }
            catch (OperationCanceledException)
            {
                Persist();
                return SessionStates.Stopped;
            }

            Persist();
        }

        if (_runner.FinishError != null)
        {
            return SessionStates.Failed;
        }

        return _record.PendingCount > 0 ? SessionStates.AwaitingApproval : SessionStates.Finished;
    }

    /// <summary>
    ///     Loads the transcript and checks that it agrees with the pending list and the interrupted batch. Throws <see cref="ErrorCode.StateConflict"/> when it does not.
    /// </summary>
    public static List<ChatMessage> Validate(SessionStore store, SessionPaths paths, SessionRecord record)
    {
        var transcript = store.LoadTranscript(paths, record.Transcript);
        ValidatePendingConsistency(transcript, record);

        var inflight = record.Inflight;
        if (inflight != null && (inflight.AssistantIndex != transcript.Count - 1 || transcript.Count == 0 || transcript[^1].Role != ChatRole.Assistant || transcript[^1].GetToolCalls().Count == 0))
        {
            throw new SubordinateException(ErrorCode.StateConflict, "The interrupted batch of tool calls does not match the end of the transcript. The session cannot be resumed safely.");
        }

        if (inflight != null)
        {
            var callIds = transcript[^1].GetToolCalls().Select(call => call.CallId).ToHashSet(StringComparer.Ordinal);

            foreach (var frame in inflight.Frames)
            {
                if (!callIds.Contains(frame.CallId))
                {
                    throw new SubordinateException(ErrorCode.StateConflict, $"The interrupted batch refers to tool call {frame.CallId}, which the last assistant message does not contain. The session cannot be resumed safely.");
                }
            }
        }

        return transcript;
    }

    private static void ValidatePendingConsistency(IReadOnlyList<ChatMessage> transcript, SessionRecord record)
    {
        var callIds = new HashSet<string>();
        var deferredResults = new Dictionary<string, string>();

        foreach (var message in transcript)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case ToolCallContent call:
                    {
                        callIds.Add(call.CallId);
                        break;
                    }
                    case ToolResultContent result when result.Output.StartsWith(PendingPrefix, StringComparison.Ordinal):
                    {
                        deferredResults[result.CallId] = result.Output;
                        break;
                    }
                }
            }
        }

        var inflightDeferred = record.Inflight?.Frames.Where(frame => frame.State == FrameStates.Deferred).ToDictionary(frame => frame.CallId, frame => frame.Text ?? string.Empty) ?? [];

        foreach (var item in record.Pending)
        {
            if (item.Status != PendingStatuses.Pending && item.Delivered)
            {
                continue;
            }

            if (!callIds.Contains(item.CallId))
            {
                throw new SubordinateException(ErrorCode.StateConflict, $"Pending item {item.Id} refers to tool call {item.CallId}, which is not in the transcript. The pending list and the session disagree, so the resume was refused.");
            }

            if (item.Status != PendingStatuses.Pending)
            {
                continue;
            }

            var recorded = deferredResults.TryGetValue(item.CallId, out var output) ? output : inflightDeferred.GetValueOrDefault(item.CallId);

            if (recorded == null || recorded != item.DeferralMessage)
            {
                throw new SubordinateException(ErrorCode.StateConflict, $"Pending item {item.Id} does not match the deferred result recorded in the transcript. The pending list and the session disagree, so the resume was refused.");
            }
        }

        foreach (var (callId, output) in deferredResults)
        {
            var id = output[PendingPrefix.Length..].Split(':', 2)[0];

            if (!record.Pending.Any(item => item.Id == id && item.CallId == callId))
            {
                throw new SubordinateException(ErrorCode.StateConflict, $"The transcript holds a pending result {id} that is missing from the pending list. The pending list and the session disagree, so the resume was refused.");
            }
        }
    }

    private async Task<HashSet<string>> RestoreInflightAsync(IReadOnlyList<ChatMessage> transcript, PendingLedger ledger)
    {
        var inflight = _record.Inflight!;
        var frames = inflight.Frames.ToDictionary(frame => frame.CallId, StringComparer.Ordinal);
        var restoredCallIds = new HashSet<string>(frames.Keys);

        await _runner!.RestoreToolCallsAsync(transcript[^1], frame =>
        {
            if (!frames.TryGetValue(frame.CallId, out var record))
            {
                return null;
            }

            switch (record.State)
            {
                case FrameStates.Succeeded:
                {
                    return RestoredToolCall.Completed(new ToolExecutionResult(frame.Tool, true, record.Text ?? string.Empty, null, null));
                }
                case FrameStates.Failed:
                {
                    return RestoredToolCall.Completed(new ToolExecutionResult(frame.Tool, false, null, record.Text ?? "The call failed.", null));
                }
                case FrameStates.Deferred:
                {
                    var item = ledger.FindByCallId(frame.CallId);

                    if (item?.Status == PendingStatuses.Approved && item.Execution == null)
                    {
                        return null;
                    }

                    if (item?.Status == PendingStatuses.Denied)
                    {
                        item.Delivered = true;
                        return RestoredToolCall.Completed(new ToolExecutionResult(frame.Tool, false, null, PendingLedger.BuildDenialMessage(item), null));
                    }

                    return RestoredToolCall.Deferred(new ToolDeferral(record.Text ?? string.Empty, item));
                }
                case FrameStates.Started:
                {
                    return RestoredToolCall.Completed(new ToolExecutionResult(frame.Tool, false, null, "This call was interrupted before it finished because the subagent process stopped. Its effects are unknown; check the current state before retrying.", null));
                }
                default:
                {
                    return null;
                }
            }
        });

        RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] restored an interrupted batch of {inflight.Frames.Count} tool calls");
        return restoredCallIds;
    }

    private async Task<string?> DeliverDecisionsAsync(SubordinateAgent agent, SubordinateGate gate, PendingLedger ledger, HashSet<string> inflightCallIds, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var lines = new List<string>();

        foreach (var item in _record.Pending.ToList())
        {
            if (item.Status == PendingStatuses.Pending || item.Delivered || inflightCallIds.Contains(item.CallId))
            {
                continue;
            }

            if (item.Status == PendingStatuses.Denied)
            {
                var reason = string.IsNullOrWhiteSpace(item.Decision?.Reason) ? string.Empty : $" Reason: {item.Decision!.Reason}.";
                lines.Add($"- DENIED {item.Id} ({item.Summary}).{reason} Do not retry it.");
                continue;
            }

            string output;

            if (item.Execution == null)
            {
                output = await ExecuteApprovedAsync(agent, gate, item, cancellationToken);
            }
            else if (item.Execution.State == "started")
            {
                output = "The approved call was started earlier but the process stopped before it finished. Its effects are unknown; check the current state before retrying.";
            }
            else
            {
                output = item.Execution.Output ?? string.Empty;
            }

            lines.Add($"- APPROVED {item.Id} ({item.Summary}), scope {item.Decision?.Scope ?? "once"}. It was run for you. Result:\n{Indent(output)}");
        }

        var stillPending = _record.Pending.Where(item => item.Status == PendingStatuses.Pending).ToList();

        if (lines.Count == 0 && _record.QueuedMessage == null)
        {
            return null;
        }

        builder.Append("[Supervisor update]\n");

        if (lines.Count > 0)
        {
            builder.Append("Decisions on your pending calls:\n");
            foreach (var line in lines)
            {
                builder.Append(line).Append('\n');
            }
        }

        if (stillPending.Count > 0)
        {
            builder.Append("Still pending: ").Append(string.Join(", ", stillPending.Select(item => $"{item.Id} ({item.Summary})"))).Append(".\n");
        }

        if (_record.QueuedMessage != null)
        {
            builder.Append("\nMessage from the supervisor:\n").Append(_record.QueuedMessage).Append('\n');
            _record.QueuedMessage = null;
        }
        else
        {
            builder.Append("\nContinue the task.\n");
        }

        return builder.ToString();
    }

    private async Task<string> ExecuteApprovedAsync(SubordinateAgent agent, SubordinateGate gate, PendingRecord item, CancellationToken cancellationToken)
    {
        if (!agent.ToolRegistry.ToolSet.Tools.TryGetValue(item.Tool, out var tool) || agent.ToolRegistry.Handlers[tool] is not ToolHandler<SubordinateContext>.Plain handler)
        {
            item.Execution = new ExecutionRecord { State = FrameStates.Failed, At = DateTimeOffset.UtcNow, Output = $"The tool {item.Tool} is not available." };
            return item.Execution.Output!;
        }

        var args = ArgumentExtractionResult.ExtractArguments(tool, TranscriptSerializer.ParseArguments(item.ArgumentsJson));
        var frame = new AgentToolFrame.PlainRunningFrame(item.CallId, tool, args);
        var verdict = await gate.EvaluateAsync(_runner!, frame, cancellationToken);

        if (verdict is not ToolGateVerdict.AllowVerdict)
        {
            var refused = verdict is ToolGateVerdict.DenyVerdict deny ? deny.Message : "The call was not allowed to run.";
            item.Execution = new ExecutionRecord { State = FrameStates.Failed, At = DateTimeOffset.UtcNow, Output = refused };
            return refused;
        }

        _lastActivity = $"running approved {item.Id}: {Shorten(item.Summary, 120)}";
        WriteStatus(SessionStates.Running);
        RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] approved {item.Id}: {Shorten(item.Summary, 200)}");

        ToolExecutionResult result;
        try
        {
            result = await handler.ExecuteAsync(_runner!, args, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ToolExecutionResult(tool, false, null, ex.Message, ex);
        }

        var text = result.IsSuccessful ? result.Output : result.FormatError();
        item.Execution = new ExecutionRecord
        {
            State = result.IsSuccessful ? FrameStates.Succeeded : FrameStates.Failed,
            At = DateTimeOffset.UtcNow,
            Output = Shorten(text, RecordedOutputLimit)
        };

        RunArtifacts.AppendLog(_runDirectory, $"  -> {Shorten(FirstLine(text), 200)}");
        _record.FilesWritten = _context!.Environment.FilesWritten.Order(StringComparer.OrdinalIgnoreCase).ToList();
        _store.Save(_paths, _record);
        return text;
    }

    private void MarkDelivered(PendingLedger ledger, HashSet<string> inflightCallIds)
    {
        foreach (var item in ledger.Record.Pending)
        {
            if (item.Status != PendingStatuses.Pending && !inflightCallIds.Contains(item.CallId))
            {
                item.Delivered = true;
            }
        }
    }

    private Task MarkExecutionStartingAsync(string callId)
    {
        _startedCallIds.Add(callId);
        _record.Counters.ToolCalls++;
        _record.Inflight = SnapshotInflight();
        _store.Save(_paths, _record);
        return Task.CompletedTask;
    }

    private void Persist()
    {
        var messages = _context!.ChatMessages;

        if (messages.Count > _persistedCount)
        {
            var newMessages = messages.Skip(_persistedCount).ToList();
            _record.Transcript = _store.AppendTranscript(_paths, _record.Transcript, newMessages);
            _persistedCount = messages.Count;
            LogMessages(newMessages);
        }

        _record.Inflight = SnapshotInflight();
        _record.FilesWritten = _context.Environment.FilesWritten.Order(StringComparer.OrdinalIgnoreCase).ToList();
        UpdateCounters();

        foreach (var frame in _runner?.ActiveToolCalls ?? [])
        {
            if (frame is AgentToolFrame.RunningFrame { Result: not null } completed && _ledger != null)
            {
                var item = _ledger.FindByCallId(completed.CallId);

                if (item?.Status == PendingStatuses.Approved && item.Execution?.State == "started")
                {
                    var text = completed.Result.IsSuccessful ? completed.Result.Output : completed.Result.FormatError();
                    item.Execution = new ExecutionRecord
                    {
                        State = completed.Result.IsSuccessful ? FrameStates.Succeeded : FrameStates.Failed,
                        At = DateTimeOffset.UtcNow,
                        Output = Shorten(text, RecordedOutputLimit)
                    };
                    item.Delivered = true;
                }
            }
        }

        _store.Save(_paths, _record);
        WriteStatus(SessionStates.Running);
    }

    private InflightRecord? SnapshotInflight()
    {
        if (_runner == null || _runner.ActiveToolCalls.Count == 0)
        {
            return null;
        }

        var record = new InflightRecord { AssistantIndex = _persistedCount - 1 };

        foreach (var frame in _runner.ActiveToolCalls)
        {
            if (frame is not AgentToolFrame.RunningFrame running)
            {
                continue;
            }

            var state = new InflightFrameRecord { CallId = running.CallId };

            if (running.Deferral != null)
            {
                state.State = FrameStates.Deferred;
                state.Text = running.Deferral.Message;
            }
            else if (running.Result != null)
            {
                state.State = running.Result.IsSuccessful ? FrameStates.Succeeded : FrameStates.Failed;
                state.Text = running.Result.IsSuccessful ? running.Result.Output : running.Result.FormatError();
            }
            else if (_startedCallIds.Contains(running.CallId) || running is AgentToolFrame.PlainRunningFrame { StartedTask: not null })
            {
                state.State = FrameStates.Started;
                _lastActivity = $"running {running.Tool.ToolId}";
            }
            else
            {
                state.State = FrameStates.NotStarted;
            }

            record.Frames.Add(state);
        }

        return record;
    }

    private void UpdateCounters()
    {
        if (_counting == null)
        {
            return;
        }

        _record.Counters.ModelCalls += _counting.ModelCalls - _countedModelCalls;
        _record.Counters.InputTokens += _counting.InputTokens - _countedInputTokens;
        _record.Counters.CachedInputTokens += _counting.CachedInputTokens - _countedCachedInputTokens;
        _record.Counters.OutputTokens += _counting.OutputTokens - _countedOutputTokens;
        _countedModelCalls = _counting.ModelCalls;
        _countedInputTokens = _counting.InputTokens;
        _countedCachedInputTokens = _counting.CachedInputTokens;
        _countedOutputTokens = _counting.OutputTokens;
    }

    private void LogMessages(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case ChatRole.Assistant:
                {
                    var calls = message.GetToolCalls();

                    foreach (var call in calls)
                    {
                        var summary = call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object && call.Arguments.TryGetProperty("command", out var command)
                            ? command.GetString() ?? string.Empty
                            : call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object && call.Arguments.TryGetProperty("path", out var path) ? path.GetString() ?? string.Empty : string.Empty;
                        RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] {call.ToolName}: {Shorten(FirstLine(summary), 200)}");
                        _lastActivity = $"{call.ToolName}: {Shorten(FirstLine(summary), 120)}";
                    }

                    if (calls.Count == 0)
                    {
                        RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] final answer ({message.GetText().Length} characters)");
                        _lastActivity = "final answer received";
                    }

                    break;
                }
                case ChatRole.Tool:
                {
                    foreach (var content in message.Contents.OfType<ToolResultContent>())
                    {
                        RunArtifacts.AppendLog(_runDirectory, $"  -> {Shorten(FirstLine(content.Output), 200)}");
                    }

                    break;
                }
                case ChatRole.User:
                {
                    RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] supervisor message ({message.GetText().Length} characters)");
                    break;
                }
            }
        }
    }

    private void Finish(string state, ErrorRecord? error)
    {
        try
        {
            if (_persistenceEnabled)
            {
                Persist();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SubordinateException)
        {
            error ??= new ErrorRecord { Code = ErrorCode.InternalError.ToToken(), Message = $"The final state could not be saved: {ex.Message}" };
            state = SessionStates.Failed;
        }

        _record.State = state;
        _record.Error = error;

        var finalText = _context?.ChatMessages.LastOrDefault();
        _record.FinalText = finalText is { Role: ChatRole.Assistant } && finalText.GetToolCalls().Count == 0 && state is SessionStates.Finished or SessionStates.AwaitingApproval
            ? finalText.GetText()
            : null;

        var run = _record.CurrentRun;
        if (run != null && run.RunId == _runId)
        {
            run.EndedAt = DateTimeOffset.UtcNow;
            run.State = state;
        }

        try
        {
            _store.Save(_paths, _record);
        }
        catch (IOException)
        {
        }

        _lastActivity = state switch
        {
            SessionStates.Finished => "finished",
            SessionStates.AwaitingApproval => $"waiting for {_record.PendingCount} approval(s)",
            SessionStates.LimitReached => _lastActivity,
            SessionStates.Stopped => "stopped",
            _ => error?.Message ?? state
        };

        try
        {
            WriteStatus(state);
            RunArtifacts.WritePending(_runDirectory, _record);

            if (run != null)
            {
                RunArtifacts.WriteResult(_runDirectory, _record, run, _record.FinalText ?? BuildFallbackText(state, error));
            }

            RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] run ended: {state}");
        }
        catch (IOException)
        {
        }
    }

    private string? BuildFallbackText(string state, ErrorRecord? error)
    {
        return state switch
        {
            SessionStates.LimitReached => $"The run stopped at a limit ({_lastActivity}) before the subagent finished. Resume the session to continue.",
            SessionStates.Stopped => "The run was stopped before the subagent finished. Resume the session to continue.",
            SessionStates.Failed => $"The run failed: {error?.Message ?? "unknown error"}",
            _ => null
        };
    }

    private void WriteStatus(string state)
    {
        try
        {
            RunArtifacts.WriteStatus(_runDirectory, new StatusSnapshot
            {
                Session = _record.Name,
                Workdir = _record.Workdir,
                RunId = _runId,
                State = state,
                ProcessId = Environment.ProcessId,
                StartedAt = _record.CurrentRun?.StartedAt ?? DateTimeOffset.UtcNow,
                ElapsedSeconds = _stopwatch.Elapsed.TotalSeconds,
                ModelCalls = _record.Counters.ModelCalls,
                ToolCalls = _record.Counters.ToolCalls,
                PendingCount = _record.PendingCount,
                InputTokens = _record.Counters.InputTokens,
                CachedInputTokens = _record.Counters.CachedInputTokens,
                OutputTokens = _record.Counters.OutputTokens,
                LastActivity = _lastActivity
            });
        }
        catch (IOException)
        {
        }
    }

    private async Task PollStopRequestAsync(CancellationTokenSource stopSource, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken);

            if (File.Exists(_paths.StopFile))
            {
                _lastActivity = "stop requested";
                await stopSource.CancelAsync();
                return;
            }
        }
    }

    private static ShellKind ParseShell(string token)
    {
        if (!ShellKindNames.TryParse(token, out var shell))
        {
            throw new SubordinateException(ErrorCode.StateConflict, $"The session records an unknown shell \"{token}\".");
        }

        return shell;
    }

    private static string Indent(string text)
    {
        var builder = new StringBuilder();

        foreach (var line in text.Split('\n'))
        {
            builder.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return index < 0 ? text : text[..index].TrimEnd('\r');
    }

    private static string Shorten(string text, int length)
    {
        return text.Length <= length ? text : text[..length] + "...";
    }

    private static string Now()
    {
        return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

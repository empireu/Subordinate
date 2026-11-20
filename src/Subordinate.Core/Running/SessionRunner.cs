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
using Subordinate.Core.Configuration;
using Subordinate.Core.Gate;
using Subordinate.Core.Monitoring;
using Subordinate.Core.Output;
using Subordinate.Core.Platform;
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
///     The supervisor update that a run delivers, with the images of the approved results it reports.
/// </summary>
public sealed record SupervisorUpdate(string Text, IReadOnlyList<ImageContent> Images);

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
    private readonly IMonitorFeed _monitor;
    private readonly CompactionSettings _compaction;
    private readonly ImageSettings _images;
    private readonly HashSet<string> _startedCallIds = [];
    private readonly HashSet<string> _reportedToolCallIds = [];
    private readonly List<int> _activeLineIndices = [];
    private readonly Stopwatch _stopwatch = new();
    private readonly object _statusLock = new();

    private CountingChatProvider? _counting;
    private AgentRunner<SubordinateContext>? _runner;
    private SubordinateContext? _context;
    private PendingLedger? _ledger;
    private bool _persistenceEnabled;
    private int _persistedCount;
    private int _countedModelCalls;
    private int _compactionCallsThisRun;
    private int _countedCompactionCalls;
    private int _summaryCallsThisRun;
    private int _deliveredImageCount;
    private bool _lastCallWasSummary;
    private int? _compactedAtModelCall;
    private long _countedInputTokens;
    private long _countedCachedInputTokens;
    private long _countedOutputTokens;
    private string _originalTask = string.Empty;
    private string _lastActivity = "starting";
    private bool _warnedAboutLimit;
    private bool _finalTurnForced;
    private bool _forceFinalTurn;
    private RunLimits? _limits;
    private DateTimeOffset? _activeToolStartedAt;
    private string? _activeToolName;
    private string? _stopRequestedBy;

    public SessionRunner(SessionStore store, SessionPaths paths, SessionRecord record, string runId, IChatProvider provider, ChatRequestOptions requestOptions, ICommandExecutor executor, IMonitorFeed? monitorFeed = null, CompactionSettings? compaction = null, ImageSettings? images = null)
    {
        _store = store;
        _paths = paths;
        _record = record;
        _runId = runId;
        _runDirectory = paths.RunDirectory(runId);
        _provider = provider;
        _monitor = monitorFeed == null ? NullMonitorFeed.Instance : new GuardedMonitorFeed(monitorFeed);
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
        _compaction = compaction ?? CompactionSettings.Disabled;
        _images = images ?? ImageSettings.Disabled;
    }

    private static string CreatePromptCacheKey(SessionRecord record)
    {
        var identity = $"{record.Name}|{record.Workdir}|{record.CreatedAt.UtcTicks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "subordinate-" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private ChatRequestOptions CreateTurnOptions()
    {
        if (!_forceFinalTurn && _record.Compaction?.Phase != CompactionPhases.Summarizing)
        {
            return _requestOptions;
        }

        return new ChatRequestOptions
        {
            Model = _requestOptions.Model,
            Temperature = _requestOptions.Temperature,
            MaxOutputTokens = _requestOptions.MaxOutputTokens,
            ReasoningEffort = _requestOptions.ReasoningEffort,
            ToolChoice = "none",
            PromptCacheKey = _requestOptions.PromptCacheKey
        };
    }

    public string RunDirectory => _runDirectory;

    public async Task<RunOutcome> RunAsync(RunLimits limits, CancellationToken cancellationToken)
    {
        _monitor.Start();

        try
        {
            return await RunAsyncCore(limits, cancellationToken);
        }
        finally
        {
            _monitor.Dispose();
        }
    }

    private async Task<RunOutcome> RunAsyncCore(RunLimits limits, CancellationToken cancellationToken)
    {
        _stopwatch.Start();
        _limits = limits;
        _record.FinalText = null;
        _record.StoppedBy = null;
        Directory.CreateDirectory(_runDirectory);

        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var pollerSource = new CancellationTokenSource();
        using var heartbeatSource = new CancellationTokenSource();
        var poller = PollStopRequestAsync(stopSource, pollerSource.Token);
        var heartbeat = HeartbeatAsync(heartbeatSource.Token);
        string state;
        ErrorRecord? error = null;

        try
        {
            state = await RunCoreAsync(limits, stopSource.Token);
        }
        catch (SubordinateException ex)
        {
            if (stopSource.IsCancellationRequested)
            {
                state = SessionStates.Stopped;
            }
            else
            {
                state = SessionStates.Failed;
                error = new ErrorRecord { Code = ex.Code.ToToken(), Message = ex.Message };
            }
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
            await heartbeatSource.CancelAsync();

            try
            {
                await poller;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }

            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
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
            DefaultShellTimeoutSeconds = _record.Limits.ShellTimeoutSeconds > 0 ? _record.Limits.ShellTimeoutSeconds : 120,
            ImagesDirectory = _paths.ImagesDirectory
        };

        environment.CountImagesInContext = CountImagesInContext;

        environment.FilesWritten.UnionWith(_record.FilesWritten);
        _context = new SubordinateContext(environment);

        var transcriptLog = _store.LoadTranscriptLog(_paths, _record.Transcript);
        var transcript = ValidateLog(transcriptLog, _record);

        if (!_images.Enabled && transcript.Any(message => message.Contents.Any(content => content is ImageContent)))
        {
            throw new SubordinateException(ErrorCode.ConfigError, $"This session contains images, but model \"{_record.Model.Alias}\" no longer sets supports_images. Set it again or start a new session.");
        }

        foreach (var message in transcript)
        {
            _context.Timeline.InsertChat(message);
        }

        _activeLineIndices.AddRange(transcriptLog.ActiveLineIndices);
        _originalTask = transcriptLog.OriginalTask;
        _persistedCount = transcript.Count;

        var ledger = new PendingLedger(_record);
        _ledger = ledger;
        var gate = new SubordinateGate(ledger, () => _runId, MarkExecutionStartingAsync) { RefuseNewCalls = RefuseToolCalls };
        var agent = new SubordinateAgent(shell, gate, _images) { KeepRunningAfterAnswer = () => _record.Compaction != null };
        _counting = new CountingChatProvider(new MonitoredChatProvider(new ImageResolvingChatProvider(_provider, _paths.ImagesDirectory, line => RunArtifacts.AppendLog(_runDirectory, line)), _monitor));
        _runner = new AgentRunner<SubordinateContext>(
            _counting,
            agent,
            null,
            _context,
            NullEventManager.Instance,
            cancellationToken,
            AgentRunner.ICompletionFactory.Wrap(CreateTurnOptions));

        var inflightCallIds = new HashSet<string>();

        if (_record.Inflight != null)
        {
            inflightCallIds = await RestoreInflightAsync(transcript, ledger);
        }

        _persistenceEnabled = true;
        var updates = await DeliverDecisionsAsync(agent, gate, ledger, inflightCallIds, cancellationToken);
        _deliveredImageCount = updates?.Images.Count ?? 0;
        var updateInserted = false;
        var modelCallsAtStart = _counting.ModelCalls;
        var warningThreshold = Math.Min(10, Math.Max(1, limits.MaxModelCalls / 4));

        _record.State = SessionStates.Running;
        Persist();

        while (true)
        {
            var atBoundary = _runner.ActiveToolCalls.Count == 0;

            if (!updateInserted && atBoundary)
            {
                updateInserted = true;

                if (updates != null)
                {
                    _context.InsertUserMessage(updates.Text, null, updates.Images);
                    _deliveredImageCount = 0;
                    MarkDelivered(ledger, inflightCallIds);
                    _record.QueuedMessage = null;
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

            if (atBoundary && _compactedAtModelCall is { } compactedAt && _counting.ModelCalls > compactedAt)
            {
                _compactedAtModelCall = null;
                VerifyCompactedContext();
            }

            var modelCallsThisRun = _counting.ModelCalls - modelCallsAtStart - _compactionCallsThisRun;
            var turnLimitFired = false;
            var compactionStarted = false;
            var compactionApplied = false;

            if (atBoundary && _record.Compaction == null && modelCallsThisRun >= limits.MaxModelCalls)
            {
                if (_finalTurnForced)
                {
                    _lastActivity = "turn limit reached";
                    return SessionStates.LimitReached;
                }

                _finalTurnForced = true;
                _forceFinalTurn = true;

                if (_record.CurrentRun != null)
                {
                    _record.CurrentRun.FinalTurnForced = true;
                }

                _lastActivity = "final turn requested";
                _context.InsertUserMessage("[Supervisor update]\nThe turn limit is reached. Do not call tools. Give your final answer now, including what is done and what remains.");
                Persist();
                turnLimitFired = true;
            }

            if (atBoundary && !turnLimitFired)
            {
                if (_record.Compaction == null && !_forceFinalTurn && TryStartCompaction())
                {
                    compactionStarted = true;
                }

                if (_record.Compaction?.Phase == CompactionPhases.WindDown && !compactionStarted
                    && (_record.Compaction.WindDownTurnsUsed >= _compaction.WindDownTurns || IsAtSafePoint()))
                {
                    _record.Compaction.Phase = CompactionPhases.Summarizing;
                }

                if (_record.Compaction?.Phase == CompactionPhases.Summarizing)
                {
                    if (_lastCallWasSummary)
                    {
                        var lastMessage = LastActiveMessage();
                        var usable = lastMessage is { Role: ChatRole.Assistant }
                                     && lastMessage.GetToolCalls().Count == 0
                                     && !string.IsNullOrWhiteSpace(lastMessage.GetText())
                                     && _counting.LastFinishReason != ChatFinishReason.Length;

                        if (usable)
                        {
                            ApplyCompaction(lastMessage!.GetText().Trim());
                            compactionApplied = true;
                        }
                        else if (_summaryCallsThisRun >= 2)
                        {
                            throw new SubordinateException(ErrorCode.CompactionFailed, "The context compaction failed because the model did not write a usable summary. Resume the session to try again.");
                        }
                    }

                    if (!compactionApplied)
                    {
                        var lastMessage = LastActiveMessage();
                        var requestIsLast = lastMessage is { Role: ChatRole.User } && lastMessage.AuthorName == CompactionAuthors.Request;

                        if (!requestIsLast)
                        {
                            _context.InsertUserMessage(BuildCompactionRequestText(), CompactionAuthors.Request);
                            _lastActivity = "compaction: writing the summary";
                            Persist();
                        }
                    }
                }
            }

            if (atBoundary && _record.Compaction == null && !turnLimitFired && !compactionStarted && limits.MaxModelCalls > 2 && !_warnedAboutLimit && modelCallsThisRun >= limits.MaxModelCalls - warningThreshold)
            {
                _warnedAboutLimit = true;
                var remaining = Math.Max(0, limits.MaxModelCalls - modelCallsThisRun);
                _lastActivity = $"{remaining} model turns left";
                _context.InsertUserMessage($"[Supervisor update]\nYou have {remaining} model turns left. Start converging on the final answer and stop exploring.");
                Persist();
            }

            if (atBoundary)
            {
                if (_record.Compaction?.Phase == CompactionPhases.WindDown)
                {
                    _lastActivity = "compaction: winding down";
                }
                else if (_record.Compaction?.Phase == CompactionPhases.Summarizing)
                {
                    _lastActivity = "compaction: writing the summary";
                }
                else if (!compactionApplied)
                {
                    _lastActivity = _forceFinalTurn ? "waiting for the final answer" : "waiting for the model";
                }

                WriteStatus(SessionStates.Running);
            }

            var modelCall = _runner.ActiveToolCalls.Count == 0;
            var callPhase = _record.Compaction?.Phase;

            if (modelCall)
            {
                _lastCallWasSummary = callPhase == CompactionPhases.Summarizing;

                if (_record.Compaction != null)
                {
                    _compactionCallsThisRun++;
                }

                if (_lastCallWasSummary)
                {
                    _summaryCallsThisRun++;
                }
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

            if (modelCall && callPhase == CompactionPhases.WindDown && _record.Compaction?.Phase == CompactionPhases.WindDown)
            {
                _record.Compaction.WindDownTurnsUsed++;
            }

            Persist();
        }

        if (_runner.FinishError != null)
        {
            return cancellationToken.IsCancellationRequested ? SessionStates.Stopped : SessionStates.Failed;
        }

        return _record.PendingCount > 0 ? SessionStates.AwaitingApproval : SessionStates.Finished;
    }

    private string BuildCompactionRequestText()
    {
        if (_context!.ChatMessages.Any(message => message.Contents.Any(content => content is ImageContent)))
        {
            return _compaction.RequestText + "\n\n" + CompactionPrompts.ImagesRequestParagraph;
        }

        return _compaction.RequestText;
    }

    private int CountImagesInContext()
    {
        var count = 0;

        foreach (var message in _context?.ChatMessages ?? [])
        {
            count += message.Contents.Count(content => content is ImageContent);
        }

        foreach (var frame in _runner?.ActiveToolCalls ?? [])
        {
            if (frame is AgentToolFrame.RunningFrame { Result: { IsSuccessful: true } result })
            {
                count += result.Images.Count;
            }
        }

        return count + _deliveredImageCount;
    }

    private string? RefuseToolCalls()
    {
        if (_record.Compaction?.Phase == CompactionPhases.Summarizing)
        {
            return "NOT EXECUTED: tools are disabled while you write the compaction summary. Write the summary as plain text.";
        }

        if (_forceFinalTurn)
        {
            return "NOT EXECUTED: the turn limit is reached, so no more tools can run. Give your final answer now, including what is done and what remains.";
        }

        return null;
    }

    private void VerifyCompactedContext()
    {
        if (_compaction.Threshold is not { } threshold)
        {
            return;
        }

        var compactedTokens = _counting!.LastUsageReported ? _counting.LastInputTokens : _record.ContextTokens;

        if (compactedTokens >= threshold)
        {
            throw new SubordinateException(ErrorCode.CompactionFailed, $"The context is still about {compactedTokens.ToString(CultureInfo.InvariantCulture)} tokens after compaction, which is not below the autocompact threshold of {threshold.ToString(CultureInfo.InvariantCulture)} tokens. Raise autocompact_threshold for this model, then resume the session.");
        }
    }

    private bool IsAtSafePoint()
    {
        var lastMessage = LastActiveMessage();
        return lastMessage is { Role: ChatRole.Assistant } && lastMessage.GetToolCalls().Count == 0;
    }

    private ChatMessage? LastActiveMessage()
    {
        var messages = _context!.ChatMessages;
        return messages.Count == 0 ? null : messages[^1];
    }

    private bool TryStartCompaction()
    {
        if (_compaction.Threshold is not { } threshold)
        {
            return false;
        }

        var estimate = ContextEstimator.EstimateActiveContext(_context!.ChatMessages, _record.ContextTokens);

        if (estimate < threshold)
        {
            return false;
        }

        if (_compaction.WindDownTurns == 0)
        {
            _record.Compaction = new CompactionRecord
            {
                Phase = CompactionPhases.Summarizing,
                StartedAt = DateTimeOffset.UtcNow,
                TokensAtStart = estimate
            };
            _lastActivity = "compaction: writing the summary";
            RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] context compaction started without wind-down (about {estimate} tokens)");
            return true;
        }

        _context.InsertUserMessage(_compaction.NoticeText, CompactionAuthors.Notice);
        _record.Compaction = new CompactionRecord
        {
            Phase = CompactionPhases.WindDown,
            StartedAt = DateTimeOffset.UtcNow,
            TokensAtStart = estimate
        };
        _lastActivity = "compaction: winding down";
        RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] context compaction started: winding down for {_compaction.WindDownTurns} turns (about {estimate} tokens)");
        Persist();
        return true;
    }

    private void ApplyCompaction(string summaryText)
    {
        Persist();

        var messages = _context!.ChatMessages;
        var noticeIndex = -1;

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.User && messages[index].AuthorName == CompactionAuthors.Notice)
            {
                noticeIndex = index;
                break;
            }
        }

        var requestIndex = -1;

        for (var index = noticeIndex + 1; index < messages.Count; index++)
        {
            if (messages[index].Role == ChatRole.User && messages[index].AuthorName == CompactionAuthors.Request)
            {
                requestIndex = index;
                break;
            }
        }

        var keptStart = noticeIndex >= 0 ? noticeIndex + 1 : messages.Count;
        var keptEnd = requestIndex >= 0 ? requestIndex : messages.Count;

        while (keptEnd > keptStart && messages[keptEnd - 1].Role == ChatRole.Assistant && messages[keptEnd - 1].GetToolCalls().Count == 0)
        {
            keptEnd--;
        }

        var keptMessages = new List<ChatMessage>();
        var keptLineIndices = new List<int>();

        for (var index = keptStart; index < keptEnd; index++)
        {
            keptMessages.Add(CompactionImages.StripImages(messages[index]));
            keptLineIndices.Add(_activeLineIndices[index]);
        }

        int keptFromLine;
        int keptToLine;

        if (keptLineIndices.Count > 0)
        {
            keptFromLine = keptLineIndices[0];
            keptToLine = keptLineIndices[^1] + 1;
        }
        else
        {
            var requestLineIndex = requestIndex >= 0 ? _activeLineIndices[requestIndex] : _activeLineIndices[^1];
            keptFromLine = requestLineIndex;
            keptToLine = requestLineIndex;
        }

        var replacementText = BuildCompactionMessage(summaryText);
        var marker = new CompactionMarker
        {
            At = DateTimeOffset.UtcNow,
            RemovedMessages = messages.Count - 1 - keptMessages.Count,
            TokensBefore = _record.Compaction!.TokensAtStart,
            Message = replacementText,
            KeptFromLine = keptFromLine,
            KeptToLine = keptToLine
        };
        var systemMessage = messages.Count > 0 ? messages[0] : null;

        _record.Transcript = _store.AppendCompaction(_paths, _record.Transcript, marker);

        _context.Timeline.ClearAll();

        if (systemMessage != null)
        {
            _context.Timeline.InsertChat(systemMessage);
        }

        _context.Timeline.InsertChat(new ChatMessage
        {
            Role = ChatRole.User,
            AuthorName = CompactionAuthors.Summary,
            Contents = [new TextContent { Text = replacementText }]
        });

        foreach (var keptMessage in keptMessages)
        {
            _context.Timeline.InsertChat(keptMessage);
        }

        _persistedCount = _context.ChatMessages.Count;
        _activeLineIndices.Clear();
        _activeLineIndices.Add(0);
        _activeLineIndices.Add(-1);

        for (var line = keptFromLine; line < keptToLine; line++)
        {
            _activeLineIndices.Add(line);
        }

        _record.Compaction = null;
        _record.Compactions++;
        _compactedAtModelCall = _counting!.ModelCalls;
        var after = ContextEstimator.Estimate(_context.ChatMessages);
        _record.ContextTokens = after;
        _store.Save(_paths, _record);

        RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] context compacted: {marker.RemovedMessages} messages removed, about {marker.TokensBefore} tokens before, about {after} tokens after; summary call input {_counting!.LastInputTokens} tokens, cached {_counting.LastCachedInputTokens} tokens");
        _lastActivity = "context compacted";
        WriteStatus(SessionStates.Running);
    }

    private string BuildCompactionMessage(string summaryText)
    {
        var builder = new StringBuilder();
        builder.Append("[Context compaction]\n");
        builder.Append("Subordinate removed the earlier messages of this session to free context. Below are the original task, your summary of the work so far, and state recorded by Subordinate. The messages after this one are your most recent turns, kept as they were. Continue the task from here, and do not redo work the summary marks as done.\n");
        builder.Append("\n# Original task\n\n").Append(_originalTask).Append('\n');
        builder.Append("\n# Your summary\n\n").Append(summaryText).Append('\n');

        var pending = _record.Pending.Where(item => item.Status == PendingStatuses.Pending).ToList();
        var denied = _record.Pending.Where(item => item.Status == PendingStatuses.Denied).ToList();

        if (pending.Count > 0 || denied.Count > 0)
        {
            builder.Append("\n# Approvals\n\n");

            if (pending.Count > 0)
            {
                builder.Append("Still pending: ").Append(string.Join(", ", pending.Select(item => $"{item.Id} ({item.Summary})"))).Append(".\n");
            }

            foreach (var item in denied)
            {
                builder.Append("Denied, do not retry: ").Append(item.Id).Append(" (").Append(item.Summary).Append(").");

                if (!string.IsNullOrWhiteSpace(item.Decision?.Reason))
                {
                    builder.Append(" Reason: ").Append(item.Decision!.Reason!.Trim()).Append('.');
                }

                builder.Append('\n');
            }
        }

        var files = _context!.Environment.FilesWritten.Order(PlatformServices.Current.Paths.Comparer).ToList();

        if (files.Count > 0)
        {
            builder.Append("\n# Files written in this session\n\n");

            foreach (var file in files.Take(200))
            {
                builder.Append("- ").Append(file).Append('\n');
            }

            if (files.Count > 200)
            {
                builder.Append("- and ").Append(files.Count - 200).Append(" more\n");
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>
    ///     Loads the transcript and checks that it agrees with the pending list and the interrupted batch. Throws <see cref="ErrorCode.StateConflict"/> when it does not.
    /// </summary>
    public static List<ChatMessage> Validate(SessionStore store, SessionPaths paths, SessionRecord record)
    {
        return ValidateLog(store.LoadTranscriptLog(paths, record.Transcript), record);
    }

    private static List<ChatMessage> ValidateLog(TranscriptLog log, SessionRecord record)
    {
        var transcript = log.ActiveMessages;
        ValidatePendingConsistency(log.AllMessages, record);

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
                    return RestoredToolCall.Completed(new ToolExecutionResult(frame.Tool, true, record.Text ?? string.Empty, null, null, RecordedImages(record.Images)));
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

                    if (item?.Status == PendingStatuses.Closed)
                    {
                        item.Delivered = true;
                        return RestoredToolCall.Completed(new ToolExecutionResult(frame.Tool, false, null, PendingLedger.BuildClosedMessage(item), null));
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

    private async Task<SupervisorUpdate?> DeliverDecisionsAsync(SubordinateAgent agent, SubordinateGate gate, PendingLedger ledger, HashSet<string> inflightCallIds, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var lines = new List<string>();
        var images = new List<ImageContent>();
        _deliveredImageCount = 0;

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
                var execution = await ExecuteApprovedAsync(agent, gate, item, cancellationToken);
                output = execution.Text;
                images.AddRange(execution.Images);
            }
            else if (item.Execution.State == "started")
            {
                output = "The approved call was started earlier but the process stopped before it finished. Its effects are unknown; check the current state before retrying.";
            }
            else
            {
                output = item.Execution.Output ?? string.Empty;
                images.AddRange(RecordedImages(item.Execution));
            }

            _deliveredImageCount = images.Count;

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
        }
        else
        {
            builder.Append("\nContinue the task.\n");
        }

        return new SupervisorUpdate(builder.ToString(), images);
    }

    private static IReadOnlyList<ImageContent> RecordedImages(ExecutionRecord record)
    {
        if (record.Images == null || record.Images.Count == 0)
        {
            return [];
        }

        return record.Images.Select(url => new ImageContent { Url = url }).ToArray();
    }

    private static IReadOnlyList<ImageContent> RecordedImages(List<string>? images)
    {
        if (images == null || images.Count == 0)
        {
            return [];
        }

        return images.Select(url => new ImageContent { Url = url }).ToArray();
    }

    private async Task<(string Text, IReadOnlyList<ImageContent> Images)> ExecuteApprovedAsync(SubordinateAgent agent, SubordinateGate gate, PendingRecord item, CancellationToken cancellationToken)
    {
        if (!agent.ToolRegistry.ToolSet.Tools.TryGetValue(item.Tool, out var tool) || agent.ToolRegistry.Handlers[tool] is not ToolHandler<SubordinateContext>.Plain handler)
        {
            item.Execution = new ExecutionRecord { State = FrameStates.Failed, At = DateTimeOffset.UtcNow, Output = $"The tool {item.Tool} is not available." };
            return (item.Execution.Output!, []);
        }

        var args = ArgumentExtractionResult.ExtractArguments(tool, TranscriptSerializer.ParseArguments(item.ArgumentsJson));
        var frame = new AgentToolFrame.PlainRunningFrame(item.CallId, tool, args);
        var verdict = await gate.EvaluateAsync(_runner!, frame, cancellationToken);

        if (verdict is not ToolGateVerdict.AllowVerdict)
        {
            var refused = verdict is ToolGateVerdict.DenyVerdict deny ? deny.Message : "The call was not allowed to run.";
            item.Execution = new ExecutionRecord { State = FrameStates.Failed, At = DateTimeOffset.UtcNow, Output = refused };
            return (refused, []);
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
        IReadOnlyList<ImageContent> images = result.IsSuccessful ? result.Images : [];
        item.Execution = new ExecutionRecord
        {
            State = result.IsSuccessful ? FrameStates.Succeeded : FrameStates.Failed,
            At = DateTimeOffset.UtcNow,
            Output = Shorten(text, RecordedOutputLimit),
            Images = images.Count > 0 ? images.Select(image => image.Url).ToList() : null
        };

        RunArtifacts.AppendLog(_runDirectory, $"  -> {Shorten(FirstLine(text), 200)}");
        _record.FilesWritten = _context!.Environment.FilesWritten.Order(PlatformServices.Current.Paths.Comparer).ToList();
        _store.Save(_paths, _record);
        _reportedToolCallIds.Add(item.CallId);
        _monitor.Publish(new MonitorToolFinished(item.CallId, result.IsSuccessful, Truncate(FirstLine(text), MonitorProtocol.MaxFirstLineCharacters), DateTimeOffset.UtcNow));
        return (text, images);
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

        if (_record.CurrentRun is { } run)
        {
            run.Counters.ToolCalls++;
        }

        _record.Inflight = SnapshotInflight();
        _store.Save(_paths, _record);

        var tool = string.Empty;
        var summary = string.Empty;
        var item = _ledger?.FindByCallId(callId);

        if (item != null)
        {
            tool = item.Tool;
            summary = item.Summary;
        }
        else if (_context != null)
        {
            for (var index = _context.ChatMessages.Count - 1; index >= 0; index--)
            {
                var call = _context.ChatMessages[index].Contents.OfType<ToolCallContent>().LastOrDefault(candidate => candidate.CallId == callId);

                if (call != null)
                {
                    tool = call.ToolName;
                    summary = ToolCallSummary.Describe(call);
                    break;
                }
            }
        }

        _monitor.Publish(new MonitorToolStarted(callId, tool, Truncate(summary, MonitorProtocol.MaxSummaryCharacters), DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    private void Persist()
    {
        var messages = _context!.ChatMessages;

        if (messages.Count > _persistedCount)
        {
            var newMessages = messages.Skip(_persistedCount).ToList();
            var firstLineIndex = _record.Transcript.Count;
            _record.Transcript = _store.AppendTranscript(_paths, _record.Transcript, newMessages);

            for (var offset = 0; offset < newMessages.Count; offset++)
            {
                _activeLineIndices.Add(firstLineIndex + offset);
            }

            _persistedCount = messages.Count;
            LogMessages(newMessages);
        }

        _record.Inflight = SnapshotInflight();
        _record.FilesWritten = _context.Environment.FilesWritten.Order(PlatformServices.Current.Paths.Comparer).ToList();
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
                        Output = Shorten(text, RecordedOutputLimit),
                        Images = completed.Result.Images.Count > 0 ? completed.Result.Images.Select(image => image.Url).ToList() : null
                    };
                    item.Delivered = true;
                }
            }
        }

        _store.Save(_paths, _record);
        PublishToolFinishes();
        _monitor.Publish(new MonitorTranscriptCommitted(_record.Transcript.Count, _record.Transcript.Bytes));
        TryWritePending();
        WriteStatus(SessionStates.Running);
    }

    private void PublishToolFinishes()
    {
        foreach (var frame in _runner?.ActiveToolCalls ?? [])
        {
            if (frame is not AgentToolFrame.RunningFrame running)
            {
                continue;
            }

            if (running.Result != null)
            {
                if (_reportedToolCallIds.Add(running.CallId))
                {
                    var text = running.Result.IsSuccessful ? running.Result.Output : running.Result.FormatError();
                    _monitor.Publish(new MonitorToolFinished(running.CallId, running.Result.IsSuccessful, Truncate(FirstLine(text), MonitorProtocol.MaxFirstLineCharacters), DateTimeOffset.UtcNow));
                }
            }
            else if (running.Deferral != null)
            {
                if (_reportedToolCallIds.Add(running.CallId))
                {
                    _monitor.Publish(new MonitorToolFinished(running.CallId, false, Truncate(FirstLine(running.Deferral.Message), MonitorProtocol.MaxFirstLineCharacters), DateTimeOffset.UtcNow));
                }
            }
        }
    }

    private InflightRecord? SnapshotInflight()
    {
        if (_runner == null || _runner.ActiveToolCalls.Count == 0)
        {
            _activeToolStartedAt = null;
            _activeToolName = null;
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

                if (running.Result.IsSuccessful && running.Result.Images.Count > 0)
                {
                    state.Images = running.Result.Images.Select(image => image.Url).ToList();
                }
            }
            else if (_startedCallIds.Contains(running.CallId) || running is AgentToolFrame.PlainRunningFrame { StartedTask: not null })
            {
                state.State = FrameStates.Started;
                _activeToolStartedAt ??= DateTimeOffset.UtcNow;
                _activeToolName = running.Tool.ToolId;
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

        var modelCalls = _counting.ModelCalls - _countedModelCalls;
        var compactionCalls = _compactionCallsThisRun - _countedCompactionCalls;
        var inputTokens = _counting.InputTokens - _countedInputTokens;
        var cachedInputTokens = _counting.CachedInputTokens - _countedCachedInputTokens;
        var outputTokens = _counting.OutputTokens - _countedOutputTokens;

        _record.Counters.ModelCalls += modelCalls;
        _record.Counters.CompactionModelCalls += compactionCalls;
        _record.Counters.InputTokens += inputTokens;
        _record.Counters.CachedInputTokens += cachedInputTokens;
        _record.Counters.OutputTokens += outputTokens;

        if (_record.CurrentRun is { } run)
        {
            run.Counters.ModelCalls += modelCalls;
            run.Counters.CompactionModelCalls += compactionCalls;
            run.Counters.InputTokens += inputTokens;
            run.Counters.CachedInputTokens += cachedInputTokens;
            run.Counters.OutputTokens += outputTokens;
        }

        if (modelCalls > 0 && _counting.LastUsageReported)
        {
            _record.ContextTokens = _counting.LastInputTokens + _counting.LastOutputTokens;
        }

        _countedModelCalls = _counting.ModelCalls;
        _countedCompactionCalls = _compactionCallsThisRun;
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
                        var summary = ToolCallSummary.Describe(call);
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
        _record.StoppedBy = state == SessionStates.Stopped ? _stopRequestedBy : null;

        if (_record.StoppedBy == StopRequesters.User && _record.QueuedMessage?.Contains(StopRequest.UserStopAgentNote, StringComparison.Ordinal) != true)
        {
            _record.QueuedMessage = _record.QueuedMessage == null ? StopRequest.UserStopAgentNote : _record.QueuedMessage + "\n\n" + StopRequest.UserStopAgentNote;
        }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (run != null)
        {
            UsageLog.Append(_store.Home, _record, run);
        }

        _lastActivity = state switch
        {
            SessionStates.Finished => "finished",
            SessionStates.AwaitingApproval => $"waiting for {_record.PendingCount} approval(s)",
            SessionStates.LimitReached => _lastActivity,
            SessionStates.Stopped => _record.StoppedBy == StopRequesters.User ? "stopped by the user" : "stopped",
            _ => error?.Message ?? state
        };

        WriteStatus(state);
        TryWritePending();

        try
        {
            if (run != null)
            {
                RunArtifacts.WriteResult(_runDirectory, _record, run, _record.FinalText ?? BuildFallbackText(state, error));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        RunArtifacts.AppendLog(_runDirectory, $"- [{Now()}] run ended: {state}");
        _monitor.Publish(new MonitorRunEnded(state, error?.Message));
    }

    private string? BuildFallbackText(string state, ErrorRecord? error)
    {
        return state switch
        {
            SessionStates.LimitReached => $"The run stopped at a limit ({_lastActivity}) before the subagent finished. Resume the session to continue.",
            SessionStates.Stopped => _record.StoppedBy == StopRequesters.User
                ? StopRequest.UserStopNotice
                : "The run was stopped before the subagent finished. Resume the session to continue.",
            SessionStates.Failed => $"The run failed: {error?.Message ?? "unknown error"}",
            _ => null
        };
    }

    private void WriteStatus(string state)
    {
        lock (_statusLock)
        {
            try
            {
                if (_activeToolStartedAt is { } started && _activeToolName is { } name)
                {
                    _lastActivity = $"running {name} for {(DateTimeOffset.UtcNow - started).TotalSeconds:F0}s";
                }

                RunArtifacts.WriteStatus(_runDirectory, new StatusSnapshot
                {
                    Session = _record.Name,
                    Workdir = _record.Workdir,
                    RunId = _runId,
                    State = state,
                    ProcessId = Environment.ProcessId,
                    StartedAt = _record.CurrentRun?.StartedAt ?? DateTimeOffset.UtcNow,
                    ElapsedSeconds = _stopwatch.Elapsed.TotalSeconds,
                    PendingCount = _record.PendingCount,
                    RunCounters = _record.CurrentRun?.Counters ?? new CountersRecord(),
                    SessionCounters = _record.Counters,
                    TurnsRemaining = TurnsRemaining(),
                    LastActivity = _lastActivity,
                    ContextTokens = _record.ContextTokens
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }

            _monitor.Publish(new MonitorStatus(
                state,
                Math.Round(_stopwatch.Elapsed.TotalSeconds, 1),
                TurnsRemaining(),
                _record.PendingCount,
                _lastActivity,
                ToMonitorCounters(_record.CurrentRun?.Counters ?? new CountersRecord()),
                ToMonitorCounters(_record.Counters),
                _record.ContextTokens));
        }
    }

    private static MonitorStatusCounters ToMonitorCounters(CountersRecord counters)
    {
        return new MonitorStatusCounters(counters.ModelCalls, counters.ToolCalls, counters.InputTokens, counters.CachedInputTokens, counters.OutputTokens);
    }

    private void TryWritePending()
    {
        try
        {
            RunArtifacts.WritePending(_runDirectory, _record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private int TurnsRemaining()
    {
        if (_limits == null || _limits.MaxModelCalls <= 0)
        {
            return 0;
        }

        var counters = _record.CurrentRun?.Counters;
        var used = Math.Max(0, (counters?.ModelCalls ?? 0) - (counters?.CompactionModelCalls ?? 0));
        return Math.Max(0, _limits.MaxModelCalls - used);
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken);
            WriteStatus(SessionStates.Running);
        }
    }

    private async Task PollStopRequestAsync(CancellationTokenSource stopSource, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken);

            if (File.Exists(_paths.StopFile))
            {
                _stopRequestedBy = StopRequest.ReadRequester(_paths);
                _lastActivity = _stopRequestedBy == StopRequesters.User ? "stop requested by the user" : "stop requested";
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

    private static string Truncate(string text, int length)
    {
        return text.Length <= length ? text : text[..length];
    }

    private static string Now()
    {
        return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
}

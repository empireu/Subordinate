using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using KnowledgeSystem.Agents.Context;
using KnowledgeSystem.Agents.Orchestration.RunnerEvents;
using KnowledgeSystem.Agents.Orchestration.Tools;
using KnowledgeSystem.Agents.Telemetry;
using KnowledgeSystem.Agents.Tools;
using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Events.Api;
using KnowledgeSystems.Extensions;

// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable UnusedAutoPropertyAccessor.Local

namespace KnowledgeSystem.Agents.Orchestration;

public abstract class AgentRunner
{
    /// <summary>
    ///     The observer for the whole execution tree, inherited from the parent.
    /// </summary>
    public abstract IEventReceiverRegistry EventReceiverRegistry { get; }

    /// <summary>
    ///     The chat provider (either inherited from the parent, or a fresh one).
    /// </summary>
    public IChatProvider Client { get; }

    /// <summary>
    ///     The parent execution context. Null if this is the root of the execution tree.
    /// </summary>
    public AgentRunner? Parent { get; }

    /// <summary>
    ///     The agent this runner deals with.
    /// </summary>
    public abstract Agent Agent { get; }

    /// <summary>
    ///     The agent runner at the top of the chain.
    ///     Will return this runner if <see cref="Parent"/> is null.
    /// </summary>
    public AgentRunner TopMostRunner { get; }

    private protected readonly string _messageId = Guid.NewGuid().ToString("D");
    private protected int _completionCallIndex;

    protected readonly List<AgentToolFrame> ToolCallsInternal = [];

    /// <summary>
    ///     Gets the ongoing tool calls.
    /// </summary>
    public IReadOnlyList<AgentToolFrame> ActiveToolCalls => ToolCallsInternal;

    protected readonly ICompletionFactory CompletionFactory;

    protected AgentRunner(IChatProvider client, AgentRunner? parent, ICompletionFactory? completionFactory)
    {
        Client = client;
        Parent = parent;

        var top = this;
        while (top.Parent != null)
        {
            top = top.Parent;
        }

        TopMostRunner = top;
        CompletionFactory = completionFactory ?? DefaultCompletionFactory.Instance;
    }

    /// <summary>
    ///     If true, the execution finished. Can be either successful, or errored. To see which, check <see cref="FinishError"/>, which is always non-null when the turn completed with error.
    /// </summary>
    public bool IsFinished { get; protected set; }

    /// <summary>
    ///     The error. Set when a turn completes with a critical error.
    /// </summary>
    public AgentExecutionError? FinishError { get; protected set; }

    public enum TurnStatus
    {
        /// <summary>
        ///     Indicates the agent started running tools.
        /// </summary>
        ToolCallsReceived,
        /// <summary>
        ///     The current tool calls have been stepped.
        /// </summary>
        ToolsStepped,
        /// <summary>
        ///     The current tool calls have finished.
        /// </summary>
        ToolsFinished,
        /// <summary>
        ///     Indicates the execution completed successfully, and the agent can be discarded.
        /// </summary>
        CompletedSuccessfully,
        /// <summary>
        ///     Indicates the execution completed with error.
        /// </summary>
        CompletedWithError,
        /// <summary>
        ///     Indicates that a non-tool-call completion was handled by the agent.
        /// </summary>
        CompletionHandled
    }

    public interface ICompletionFactory
    {
        /// <summary>
        ///     Creates the chat request options for a turn.
        /// </summary>
        ChatRequestOptions CreateOptionsForTurn(AgentRunner runner);

        public static ICompletionFactory Wrap(Func<AgentRunner, ChatRequestOptions> factory)
        {
            return new Wrapper(factory);
        }

        public static ICompletionFactory Wrap(Func<ChatRequestOptions> factory)
        {
            return new Wrapper(_ => factory());
        }

        private sealed class Wrapper(Func<AgentRunner, ChatRequestOptions> factory) : ICompletionFactory
        {
            public ChatRequestOptions CreateOptionsForTurn(AgentRunner runner)
            {
                return factory(runner);
            }
        }
    }

    public sealed class DefaultCompletionFactory : ICompletionFactory
    {
        public static readonly DefaultCompletionFactory Instance = new();

        private DefaultCompletionFactory() { }

        public ChatRequestOptions CreateOptionsForTurn(AgentRunner runner)
        {
            return new ChatRequestOptions();
        }
    }
}

/// <summary>
///     Constructs an agent execution engine.
///     The engine is meant to execute a full turn, including all tool calls required for the turn.
/// </summary>
/// <typeparam name="TContext">Specific context class. Holds the chat history and specialized data for sub-agents.</typeparam>
public sealed class AgentRunner<TContext> : AgentRunner where TContext : AgentExecutionContext
{
    private readonly IEventManager _eventManager;
    public readonly CancellationToken CancellationToken;

    private static readonly TimeSpan ThinkingEmitInterval = TimeSpan.FromSeconds(2);
    private const int MinThinkingEmitDeltaChars = 120;

    /// <summary>
    ///     Constructs an agent execution engine.
    ///     The engine is meant to execute a full turn, including all tool calls required for the turn.
    /// </summary>
    /// <param name="client">The chat provider.</param>
    /// <param name="agent">The agent being executed.</param>
    /// <param name="parent">The parent execution engine, if this is a sub-agent.</param>
    /// <param name="context">The specific execution context.</param>
    /// <param name="cancellationToken"><b>Cancellation token that is valid throughout the lifetime of the runner (stored in various places).</b></param>
    /// <param name="eventManager">The event manager implementation. Will be used to dispatch all core events.</param>
    /// <param name="completionFactory">Optional factory to configure the completion options.</param>
    /// <typeparam name="TContext">Specific context class. Holds the chat history and specialized data for sub-agents.</typeparam>
    public AgentRunner(
        IChatProvider client,
        Agent<TContext> agent,
        AgentRunner? parent,
        TContext context,
        IEventManager eventManager,
        CancellationToken cancellationToken,
        ICompletionFactory? completionFactory = null
    ) : base(client, parent, completionFactory)
    {
        _eventManager = eventManager;

        CancellationToken = cancellationToken;
        Agent = agent;
        ExecutionContext = context;
    }

    public override IEventReceiverRegistry EventReceiverRegistry => _eventManager;

    public IEventManager EventManager => _eventManager;

    public override Agent<TContext> Agent { get; }

    public TContext ExecutionContext { get; }

    /// <summary>
    ///     Runs the agent to completion, looping <see cref="ExecuteTurn"/> until finished.
    /// </summary>
    public async Task<int> RunAsync()
    {
        var turns = 0;
        while (!IsFinished)
        {
            CancellationToken.ThrowIfCancellationRequested();
            await ExecuteTurn();
            ++turns;
        }

        return turns;
    }

    /// <summary>
    ///     Executes one "turn". This is somewhat finely-grained; has multiple stop points (all the values in <see cref="AgentRunner.TurnStatus"/>).
    /// </summary>
    /// <returns></returns>
    public async Task<TurnStatus> ExecuteTurn()
    {
        var status = await ExecuteTurnCore();

        await _eventManager.SendAsync(new AgentTurnEvent(status), CancellationToken);

        return status;
    }

    private IReadOnlyDictionary<string, string>? CreateTurnMetadata(IReadOnlyDictionary<string, string>? baseMetadata)
    {
        if (baseMetadata == null || baseMetadata.Count == 0)
        {
            return baseMetadata;
        }

        var metadata = new Dictionary<string, string>(baseMetadata, StringComparer.Ordinal);

        if (!metadata.ContainsKey("message_id"))
        {
            metadata["message_id"] = _messageId;
        }

        if (_completionCallIndex > 0)
        {
            metadata["call_type"] = "secondary_call";
        }

        _completionCallIndex++;
        return metadata;
    }

    private async Task<TurnStatus> ExecuteTurnCore()
    {
        if (IsFinished)
        {
            throw new Exception("Tried to execute finished runner!");
        }

        if (ToolCallsInternal.Count > 0)
        {
            if (await StepTools())
            {
                return TurnStatus.ToolsStepped;
            }

            var callbackStatus = await HandleCallbackResult(() => Agent.HandleToolFinish(this));
            ToolCallsInternal.Clear();
            return callbackStatus ?? TurnStatus.ToolsFinished;
        }

        var turnOptions = CompletionFactory.CreateOptionsForTurn(this);

        var request = new ChatRequest
        {
            Model = turnOptions.Model,
            Messages = ExecutionContext.ChatMessages,
            Tools = Agent.ToolRegistry.ToolSet.GetTools(),
            Temperature = turnOptions.Temperature,
            MaxOutputTokens = turnOptions.MaxOutputTokens,
            ReasoningEffort = turnOptions.ReasoningEffort,
            ToolChoice = turnOptions.ToolChoice,
            PromptCacheKey = turnOptions.PromptCacheKey,
            Metadata = CreateTurnMetadata(turnOptions.Metadata)
        };

        // Executes the LLM call and raises the error and completion events:
        using (var requestTelemetry = AgentTelemetry.Agent.StartInternalActivity("GetResponse"))
        {
            try
            {
                requestTelemetry?.SetTag("agent", Agent.AgentId);

                var reasoning = new StringBuilder();
                var lastEmitTimestamp = Stopwatch.GetTimestamp();
                var lastEmittedLength = 0;

                ChatMessage? responseMessage = null;
                var finishReason = ChatFinishReason.Unknown;
                ChatUsage? usage = null;

                await foreach (var streamEvent in Client.StreamAsync(request, CancellationToken))
                {
                    switch (streamEvent)
                    {
                        case ReasoningDeltaEvent reasoningDelta:
                        {
                            reasoning.Append(reasoningDelta.Delta);

                            var grewEnough = reasoning.Length - lastEmittedLength >= MinThinkingEmitDeltaChars;
                            var timeElapsed = Stopwatch.GetElapsedTime(lastEmitTimestamp) >= ThinkingEmitInterval;

                            if (reasoning.Length > 0 && (grewEnough || timeElapsed))
                            {
                                lastEmitTimestamp = Stopwatch.GetTimestamp();
                                lastEmittedLength = reasoning.Length;

                                await _eventManager.SendAsync(new AgentThinkingEvent(reasoning.ToString()), CancellationToken);
                            }

                            break;
                        }
                        case UsageReceivedEvent usageReceived:
                        {
                            usage = usageReceived.Usage;
                            break;
                        }
                        case StreamCompletedEvent completed:
                        {
                            responseMessage = completed.Message;
                            finishReason = completed.FinishReason;
                            break;
                        }
                        case StreamErrorEvent errorEvent:
                        {
                            FinishError = new AgentExecutionError($"Chat request failed: {errorEvent.Error.Message}", true);
                            IsFinished = true;

                            await _eventManager.SendAsync(new AgentErrorEvent(FinishError), CancellationToken);
                            await _eventManager.SendAsync(new AgentCompletedEvent(), CancellationToken);

                            requestTelemetry?.SetTag("exception", errorEvent.Error.Message);
                            requestTelemetry?.SetStatus(ActivityStatusCode.Error);

                            return TurnStatus.CompletedWithError;
                        }
                    }
                }

                if (reasoning.Length > lastEmittedLength)
                {
                    await _eventManager.SendAsync(new AgentThinkingEvent(reasoning.ToString()), CancellationToken);
                }

                if (responseMessage == null)
                {
                    FinishError = new AgentExecutionError("The provider did not return a completed message.", true);
                    IsFinished = true;

                    await _eventManager.SendAsync(new AgentErrorEvent(FinishError), CancellationToken);
                    await _eventManager.SendAsync(new AgentCompletedEvent(), CancellationToken);

                    requestTelemetry?.SetStatus(ActivityStatusCode.Error);

                    return TurnStatus.CompletedWithError;
                }

                if (usage != null)
                {
                    if (usage.InputTokens.HasValue) { requestTelemetry?.SetTag("input_tokens", usage.InputTokens.Value); }
                    if (usage.CachedInputTokens.HasValue) { requestTelemetry?.SetTag("cached_input_tokens", usage.CachedInputTokens.Value); }
                    if (usage.ReasoningTokens.HasValue) { requestTelemetry?.SetTag("reasoning_tokens", usage.ReasoningTokens.Value); }
                    if (usage.OutputTokens.HasValue) { requestTelemetry?.SetTag("output_tokens", usage.OutputTokens.Value); }
                    if (usage.TotalTokens.HasValue) { requestTelemetry?.SetTag("total_tokens", usage.TotalTokens.Value); }
                }

                requestTelemetry?.SetStatus(ActivityStatusCode.Ok);

                // Tool calls required:
                if (finishReason == ChatFinishReason.ToolCalls)
                {
                    ExecutionContext.InsertAssistantCompletion(responseMessage);
                    await Agent.HandleToolCompletion(this, responseMessage);
                    var toolCalls = responseMessage.GetToolCalls();
                    await BeginToolCalls(responseMessage, toolCalls);
                    return TurnStatus.ToolCallsReceived;
                }

                // Non-tool completion. Continue with result and notify:
                await _eventManager.SendAsync(new AgentMessageEvent(responseMessage), CancellationToken);
                return await HandleCallbackResult(() => Agent.HandleCompletion(this, responseMessage)) ?? TurnStatus.CompletionHandled;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FinishError = new AgentExecutionError($"Chat request failed: {ex.Message}", true);
                IsFinished = true;

                await _eventManager.SendAsync(new AgentErrorEvent(FinishError), CancellationToken);
                await _eventManager.SendAsync(new AgentCompletedEvent(), CancellationToken);

                requestTelemetry?.SetTag("exception", ex.Message);
                requestTelemetry?.SetStatus(ActivityStatusCode.Error);

                return TurnStatus.CompletedWithError;
            }
        }
    }

    private async Task<TurnStatus?> HandleCallbackResult(Func<Task<AgentCallbackResult>> callback)
    {
        // Let the agent decide whether this finishes execution:
        AgentCallbackResult callbackResult;
        try
        {
            callbackResult = await callback();
        }
        catch (Exception ex)
        {
            callbackResult = new AgentCallbackResult(true, new AgentExecutionError($"Agent completion handler threw: {ex.Message}", true));
        }

        if (!callbackResult.CompletesExecution)
        {
            // Doesn't complete agent. Needs specific status:
            return null;
        }

        IsFinished = true;
        FinishError = callbackResult.Error;

        await _eventManager.SendAsync(new AgentCompletedEvent(), CancellationToken);

        return FinishError == null
            ? TurnStatus.CompletedSuccessfully
            : TurnStatus.CompletedWithError;
    }

    /// <summary>
    ///     Steps the current tools.
    ///     For simple tools, this will await their execution.
    ///     For sub-agents, this will execute one turn.
    ///     Currently, the calls are executed sequentially.
    /// </summary>
    /// <returns>True if more steps are needed. Otherwise, false.</returns>
    private async Task<bool> StepTools()
    {
        AgentToolFrame.RunningFrame? pendingFrame = null;
        var indexInCollection = -1;

        for (var frameIndex = 0; frameIndex < ToolCallsInternal.Count; frameIndex++)
        {
            var agentToolFrame = ToolCallsInternal[frameIndex];

            if (agentToolFrame is AgentToolFrame.RunningFrame { IsSettled: false } runningFrame)
            {
                pendingFrame = runningFrame;
                indexInCollection = frameIndex;
                break;
            }
        }

        // Frames still need execution:
        if (pendingFrame != null)
        {
            switch (pendingFrame)
            {
                case AgentToolFrame.PlainRunningFrame plainFrame:
                {
                    if (plainFrame.StartedTask == null)
                    {
                        var handler = (ToolHandler<TContext>.Plain) Agent.ToolRegistry.Handlers[plainFrame.Tool];

                        if (Agent.ToolGate != null && !await PassToolGate(plainFrame, indexInCollection))
                        {
                            return true;
                        }

                        try
                        {
                            plainFrame.StartedTask = handler.ExecuteAsync(
                                this,
                                plainFrame.Args,
                                CancellationToken
                            );
                        }
                        catch (Exception ex)
                        {
                            var errorResult = new ToolExecutionResult(plainFrame.Tool, false, null, ex.Message, ex);
                            await _eventManager.SendAsync(new AgentToolResultEvent(indexInCollection, plainFrame.Tool, errorResult), CancellationToken);
                            plainFrame.Result = errorResult;
                            return true;
                        }

                        return true;
                    }

                    ToolExecutionResult result;
                    try
                    {
                        result = await plainFrame.StartedTask;
                    }
                    catch (Exception ex)
                    {
                        result = new ToolExecutionResult(plainFrame.Tool, false, null, ex.Message, ex);
                    }

                    await _eventManager.SendAsync(
                        new AgentToolResultEvent(indexInCollection, plainFrame.Tool, result),
                        CancellationToken
                    );

                    plainFrame.Result = result;

                    return true;
                }
                case AgentToolFrame.RunningSubAgent subAgentFrame:
                {
                    if (subAgentFrame.Proxy == null)
                    {
                        var handler = (ToolHandler<TContext>.SubAgent) Agent.ToolRegistry.Handlers[subAgentFrame.Tool];

                        try
                        {
                            subAgentFrame.Proxy = await handler.BeginSubAgentExecution(
                                this,
                                subAgentFrame.Args,
                                ExecutionContext,
                                subAgentFrame.CallId,
                                CancellationToken
                            );
                        }
                        catch (Exception ex)
                        {
                            var errorResult = new ToolExecutionResult(subAgentFrame.Tool, false, null, ex.Message, ex);
                            await _eventManager.SendAsync(new AgentToolResultEvent(indexInCollection, subAgentFrame.Tool, errorResult), CancellationToken);
                            subAgentFrame.Result = errorResult;
                            return true;
                        }

                        return true;
                    }

                    ToolExecutionResult? result;
                    try
                    {
                        result = await subAgentFrame.Proxy.StepAsync();
                    }
                    catch (Exception ex)
                    {
                        result = new ToolExecutionResult(subAgentFrame.Tool, false, null, ex.Message, ex);
                    }

                    if (result != null)
                    {
                        await _eventManager.SendAsync(
                            new AgentToolResultEvent(indexInCollection, subAgentFrame.Tool, result),
                            CancellationToken
                        );

                        subAgentFrame.Result = result;
                    }

                    return true;
                }
                default:
                    throw new Exception($"Invalid tool frame {pendingFrame}");
            }
        }

        // Insert results:
        for (var index = 0; index < ToolCallsInternal.Count; index++)
        {
            var toolCall = ToolCallsInternal[index];

            ToolExecutionResult result;
            string? toolName = null;

            switch (toolCall)
            {
                case AgentToolFrame.Hallucination hallucination:
                    ExecutionContext.InsertToolResult(hallucination.CallId, hallucination.FunctionName, hallucination.ErrorMessage);
                    continue;
                case AgentToolFrame.MissingArgs missingArgs:
                    ExecutionContext.InsertToolResult(missingArgs.CallId, missingArgs.ToolName, missingArgs.ErrorMessage);
                    continue;
                case AgentToolFrame.RunningFrame { Deferral: { } deferral } deferred:
                    ExecutionContext.InsertToolResult(deferred.CallId, deferred.Tool.ToolId, deferral.Message);
                    continue;
                case AgentToolFrame.RunningFrame running:
                    result = running.Result!;
                    toolName = running.Tool.ToolId;
                    break;
                default:
                    throw new Exception($"Invalid tool frame {toolCall}");
            }

            var output = result.IsSuccessful
                ? result.Output
                : result.FormatError();

            if (result.IsSuccessful && result.Images.Count > 0)
            {
                ExecutionContext.InsertToolResult(toolCall.CallId, toolName ?? string.Empty, output, result.Images);
                continue;
            }

            ExecutionContext.InsertToolResult(toolCall.CallId, toolName ?? string.Empty, output);
        }

        return false;
    }

    /// <summary>
    ///     Asks the agent's tool gate about a frame that is about to start.
    ///     Settles the frame when the gate denies or defers, and fails closed when the gate throws.
    /// </summary>
    /// <returns>True if the call may execute. Otherwise, false.</returns>
    private async Task<bool> PassToolGate(AgentToolFrame.PlainRunningFrame frame, int indexInCollection)
    {
        ToolGateVerdict verdict;
        try
        {
            verdict = await Agent.ToolGate!.EvaluateAsync(this, frame, CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            verdict = ToolGateVerdict.Deny($"The call was not executed because the permission check failed: {ex.Message}");
        }

        switch (verdict)
        {
            case ToolGateVerdict.AllowVerdict:
            {
                return true;
            }
            case ToolGateVerdict.DeferVerdict defer:
            {
                frame.Deferral = defer.Deferral;
                await _eventManager.SendAsync(new AgentToolDeferredEvent(indexInCollection, frame.Tool, defer.Deferral), CancellationToken);
                return false;
            }
            case ToolGateVerdict.DenyVerdict deny:
            {
                var deniedResult = new ToolExecutionResult(frame.Tool, false, null, deny.Message, null);
                await _eventManager.SendAsync(new AgentToolResultEvent(indexInCollection, frame.Tool, deniedResult), CancellationToken);
                frame.Result = deniedResult;
                return false;
            }
            default:
            {
                var unknownResult = new ToolExecutionResult(frame.Tool, false, null, "The call was not executed because the permission check returned an unrecognized verdict.", null);
                await _eventManager.SendAsync(new AgentToolResultEvent(indexInCollection, frame.Tool, unknownResult), CancellationToken);
                frame.Result = unknownResult;
                return false;
            }
        }
    }

    /// <summary>
    ///     Rebuilds the tool frames of an assistant completion whose results were never inserted, for example after the process stopped mid-batch.
    ///     The completion must already be the last message in the context. Frames without a restored state are executed normally on the following turns, passing through the tool gate again.
    /// </summary>
    /// <param name="completion">The assistant message holding the tool calls.</param>
    /// <param name="restoreState">Returns the persisted state of a frame, or null if the frame still needs to execute.</param>
    public async Task RestoreToolCallsAsync(ChatMessage completion, Func<AgentToolFrame.RunningFrame, RestoredToolCall?> restoreState)
    {
        if (IsFinished)
        {
            throw new InvalidOperationException("Cannot restore tool calls on a finished runner.");
        }

        if (ToolCallsInternal.Count > 0)
        {
            throw new InvalidOperationException("Cannot restore tool calls while other tool calls are active.");
        }

        await BeginToolCalls(completion, completion.GetToolCalls());

        for (var index = 0; index < ToolCallsInternal.Count; index++)
        {
            if (ToolCallsInternal[index] is not AgentToolFrame.RunningFrame runningFrame)
            {
                continue;
            }

            var state = restoreState(runningFrame);

            if (state == null)
            {
                continue;
            }

            if (state.Result != null)
            {
                runningFrame.Result = state.Result;
            }
            else if (state.Deferral != null)
            {
                runningFrame.Deferral = state.Deferral;
            }
        }
    }

    private readonly struct ToolCall
    {
        /// <summary>
        ///     The original call from the API.
        /// </summary>
        public required ToolCallContent Call { get; init; }

        /// <summary>
        ///     The API call ID, used for writing back the history.
        /// </summary>
        public string CallId { get; init; }

        /// <summary>
        ///     The resolved tool, if the function name wasn't hallucinated.
        /// </summary>
        public required AgentTool? Tool { get; init; }

        /// <summary>
        ///     The original index in the (parallel) tool calls.
        /// </summary>
        public required int OriginalIndex { get; init; }

        /// <summary>
        ///     The extracted arguments, for non-hallucinated tools.
        /// </summary>
        public required ArgumentExtractionResult? Args { get; init; }
    }

    private async Task BeginToolCalls(ChatMessage completion, IReadOnlyList<ToolCallContent> toolCalls)
    {
        // Will hold resolved and hallucinated tools, in the error they arrived:
        var calls = new List<ToolCall>();

        // Extracts the valid tool calls and hallucinated tool calls:
        for (var callIndex = 0; callIndex < toolCalls.Count; callIndex++)
        {
            var toolCall = toolCalls[callIndex];

            if (Agent.ToolRegistry.ToolSet.TryMatchTool(toolCall, out var tool))
            {
                calls.Add(new ToolCall
                {
                    Call = toolCall,
                    CallId = toolCall.CallId,
                    Tool = tool,
                    OriginalIndex = callIndex,
                    Args = ArgumentExtractionResult.ExtractArguments(tool, toolCall.Arguments)
                });
            }
            else
            {
                calls.Add(new ToolCall
                {
                    Call = toolCall,
                    CallId = toolCall.CallId,
                    Tool = null,
                    OriginalIndex = callIndex,
                    Args = null
                });
            }
        }

        var toolEvents = new ToolCallInfo[calls.Count];

        // Matches all the events:
        for (var callIndex = 0; callIndex < calls.Count; callIndex++)
        {
            var toolCall = calls[callIndex];

            if (toolCall.Tool == null)
            {
                var functionName = toolCall.Call.ToolName;

                await _eventManager.SendAsync(
                    new AgentErrorEvent(new AgentToolHallucinationError(
                        $"Invalid tool \"{functionName}\"",
                        false,
                        functionName,
                        callIndex
                    )),
                    CancellationToken
                );
            }
            else
            {
                var args = toolCall.Args!;

                if (args.Status == ArgumentExtractionResult.ExtractionStatus.Success)
                {
                    toolEvents[callIndex] = new ToolCallInfo
                    {
                        IsValid = true,
                        Tool = toolCall.Tool,
                        Args = args
                    };
                }
                else
                {
                    await _eventManager.SendAsync(
                        new AgentErrorEvent(new AgentToolIncompleteArgumentsError(
                            $"Missing arguments for \"{toolCall.Tool.ToolId}\"",
                            false,
                            toolCall.Tool.ToolId,
                            callIndex,
                            args
                        )),
                        CancellationToken
                    );
                }
            }
        }

        // Dispatches the events, along with the completion:
        await _eventManager.SendAsync(new AgentToolCallsEvent(completion, toolEvents), CancellationToken);

        // Pushes each call to the pending list.
        // Does not start their execution yet, but it does resolve the immediate errors.
        for (var callIndex = 0; callIndex < calls.Count; callIndex++)
        {
            var toolCall = calls[callIndex];

            if (toolCall.Tool == null)
            {
                var functionName = toolCall.Call.ToolName;
                var message = Agent.GetToolHallucinationError(functionName) ??
                              GetDefaultToolHallucinationResult(functionName);

                ToolCallsInternal.Add(new AgentToolFrame.Hallucination(toolCall.CallId, functionName, message));
            }
            else
            {
                var tool = toolCall.Tool!;
                var args = toolCall.Args!;
                var handler = Agent.ToolRegistry.Handlers[tool];

                if (args.Status == ArgumentExtractionResult.ExtractionStatus.Success)
                {
                    switch (handler)
                    {
                        case ToolHandler<TContext>.Plain:
                            ToolCallsInternal.Add(new AgentToolFrame.PlainRunningFrame(toolCall.CallId, tool, args));
                            break;
                        case ToolHandler<TContext>.SubAgent:
                            ToolCallsInternal.Add(new AgentToolFrame.RunningSubAgent(toolCall.CallId, tool, args));
                            break;
                        default:
                            throw new Exception($"Invalid tool handler {handler}");
                    }
                }
                else
                {
                    var error = handler.GetMissingArgumentError(args) ??
                                GetDefaultMissingArgumentsError(args);

                    ToolCallsInternal.Add(new AgentToolFrame.MissingArgs(toolCall.CallId, tool.ToolId, error));
                }
            }
        }
    }

    private string GetDefaultToolHallucinationResult(string functionName)
    {
        // We will only mention the tool names to not blow up tokens:
        var toolList = string.Join(", ", Agent.ToolRegistry.ToolSet.Tools.Values.Select(x => x.ToolId));

        return $"Invalid tool `{functionName}`. Available tools are: {toolList}";
    }

    private static string GetDefaultMissingArgumentsError(ArgumentExtractionResult args)
    {
        var missing = string.Join(", ", args.MissingArguments.Select(a => a.ArgumentName));

        return $"Missing required arguments {missing}";
    }

    #region Helper

    public bool TryGetUniqueActiveHandlerOfType<THandler>([NotNullWhen(true)] out THandler? result) where THandler : ToolHandler
    {
        result = null;

        for (var index = 0; index < ToolCallsInternal.Count; index++)
        {
            var agentToolFrame = ToolCallsInternal[index];

            if (agentToolFrame is not AgentToolFrame.RunningFrame runningFrame)
            {
                continue;
            }

            if (Agent.ToolRegistry.Handlers[runningFrame.Tool] is not THandler handler)
            {
                continue;
            }

            if (result != null)
            {
                throw new InvalidOperationException($"Tried to get unique active handler of type {typeof(THandler)}, but found duplicates");
            }

            result = handler;
        }

        return result != null;
    }

    public bool TryGetUniqueActiveSubAgentProxyForHandler<THandler>([NotNullWhen(true)] out ISubAgentProxy? result) where THandler : ToolHandler
    {
        result = null;

        var foundHandler = false;
        for (var index = 0; index < ToolCallsInternal.Count; index++)
        {
            var agentToolFrame = ToolCallsInternal[index];

            if (agentToolFrame is not AgentToolFrame.RunningSubAgent subAgentFrame)
            {
                continue;
            }

            if (Agent.ToolRegistry.Handlers[subAgentFrame.Tool] is not THandler handler)
            {
                continue;
            }

            if (foundHandler)
            {
                throw new InvalidOperationException($"Tried to get unique active handler of type {typeof(THandler)}, but found duplicates");
            }

            foundHandler = true;
            result = subAgentFrame.Proxy ?? throw new InvalidOperationException($"Tried to get proxy for {handler} before it was available");
        }

        return result != null;
    }

    #endregion
}

using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Events.Api;

namespace KnowledgeSystem.Agents.Orchestration.RunnerEvents;

/// <summary>
///     Dispatched when the agent runs tools in a turn.
/// </summary>
public record AgentToolCallsEvent(ChatMessage Message, ToolCallInfo[] Calls) : IEvent;

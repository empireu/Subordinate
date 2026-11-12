using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Events.Api;

namespace KnowledgeSystem.Agents.Orchestration.RunnerEvents;

/// <summary>
///     Dispatched when the agent outputs a message that doesn't contain any tool calls.
/// </summary>
public record AgentMessageEvent(ChatMessage Message) : IEvent;

using KnowledgeSystem.Agents.Orchestration.Tools;
using KnowledgeSystem.Agents.Tools;
using KnowledgeSystem.Events.Api;

namespace KnowledgeSystem.Agents.Orchestration.RunnerEvents;

/// <summary>
///     Dispatched when the tool gate defers a call instead of executing it. Takes the place of <see cref="AgentToolResultEvent"/> for that call.
/// </summary>
/// <param name="IndexInCollection">The index in <see cref="AgentToolCallsEvent.Calls"/>.</param>
public record AgentToolDeferredEvent(int IndexInCollection, AgentTool Tool, ToolDeferral Deferral) : IEvent;

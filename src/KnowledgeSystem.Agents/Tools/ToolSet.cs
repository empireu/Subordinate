using System.Diagnostics.CodeAnalysis;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace KnowledgeSystem.Agents.Tools;

public interface IReadOnlyToolSet
{
    /// <summary>
    ///     Gets the tools to be sent to the LLM.
    /// </summary>
    IReadOnlyList<ChatTool> GetTools();

    /// <summary>
    ///     Tools by their tool ID.
    /// </summary>
    public IReadOnlyDictionary<string, AgentTool> Tools { get; }

    /// <summary>
    ///     Tries to match a tool from the model's response.
    /// </summary>
    /// <param name="toolCall">The raw tool call content from the response.</param>
    /// <param name="tool">If true, the tool that was matched.</param>
    /// <returns>True if a tool was matched. Otherwise, false.</returns>
    public bool TryMatchTool(ToolCallContent toolCall, [NotNullWhen(true)] out AgentTool? tool);
}

public sealed class ToolSet : IReadOnlyToolSet
{
    private readonly Dictionary<string, AgentTool> _tools = [];
    private IReadOnlyList<ChatTool>? _cachedTools;

    public IReadOnlyDictionary<string, AgentTool> Tools => _tools;

    public IReadOnlyList<ChatTool> GetTools()
    {
        if (_cachedTools != null)
        {
            return _cachedTools;
        }

        var tools = new List<ChatTool>(_tools.Count);

        foreach (var agentTool in _tools.Values)
        {
            tools.Add(agentTool.CreateChatTool());
        }

        _cachedTools = tools;

        return _cachedTools;
    }

    public bool TryMatchTool(ToolCallContent toolCall, [NotNullWhen(true)] out AgentTool? tool)
    {
        return _tools.TryGetValue(toolCall.ToolName, out tool);
    }

    /// <summary>
    ///     Adds a tool to the set.
    /// </summary>
    public void AddTool(AgentTool agentTool)
    {
        if (!_tools.TryAdd(agentTool.ToolId, agentTool))
        {
            throw new InvalidOperationException($"Duplicate tool definition {agentTool.ToolId}");
        }

        _cachedTools = null;
    }
}

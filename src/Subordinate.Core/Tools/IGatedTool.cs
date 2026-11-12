using KnowledgeSystem.Agents.Tools;
using Subordinate.Core.Classification;
using Subordinate.Core.Running;

namespace Subordinate.Core.Tools;

/// <summary>
///     What a pending approval record shows about a call.
/// </summary>
public sealed class CallDescription
{
    public required string Summary { get; init; }

    public string? Command { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? Path { get; init; }
}

/// <summary>
///     A tool whose calls the gate can classify. Every Subordinate tool implements this, and the gate denies calls to any tool that does not.
/// </summary>
public interface IGatedTool
{
    ActionClassification Classify(ArgumentExtractionResult args, ToolEnvironment environment);

    /// <summary>
    ///     A stable identity of the call's effect, used to make denials sticky.
    /// </summary>
    string Fingerprint(ArgumentExtractionResult args, ToolEnvironment environment);

    CallDescription Describe(ArgumentExtractionResult args, ToolEnvironment environment);
}

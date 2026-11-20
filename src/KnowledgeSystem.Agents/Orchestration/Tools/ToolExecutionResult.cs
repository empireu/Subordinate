using KnowledgeSystem.Agents.Tools;
using KnowledgeSystem.Ai.Providers.Abstractions;

namespace KnowledgeSystem.Agents.Orchestration.Tools;

/// <summary>
///     Result of executing a tool.
/// </summary>
public sealed class ToolExecutionResult
{
    /// <summary>
    ///     Result of executing a tool.
    /// </summary>
    public ToolExecutionResult(AgentTool tool, bool isSuccessful, string? output, string? errorMessage, Exception? thrownException)
        : this(tool, isSuccessful, output, errorMessage, thrownException, [])
    {
    }

    /// <summary>
    ///     Result of executing a tool, with the images it returns to the model.
    /// </summary>
    public ToolExecutionResult(AgentTool tool, bool isSuccessful, string? output, string? errorMessage, Exception? thrownException, IReadOnlyList<ImageContent> images)
    {
        if (!isSuccessful && images.Count > 0)
        {
            throw new ArgumentException("An unsuccessful tool result cannot hold images.", nameof(images));
        }

        Tool = tool;
        IsSuccessful = isSuccessful;
        Output = output;
        ErrorMessage = errorMessage;
        ThrownException = thrownException;
        Images = images;
    }

    /// <summary>
    ///     The images the tool returned. Empty unless the tool returns images.
    /// </summary>
    public IReadOnlyList<ImageContent> Images { get; }
    
    public AgentTool Tool { get; }
    
    /// <summary>
    ///     The final status of the tool.
    /// </summary>
    public bool IsSuccessful { get; }
    
    /// <summary>
    ///     The result reported to the LLM.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public string Output => IsSuccessful
        ? field ?? string.Empty 
        : throw new InvalidOperationException($"Cannot get tool result for failed call"); 
    
    /// <summary>
    ///     The formatted error message, usually when the error is well-defined by the tool.
    /// </summary>
    public string? ErrorMessage => !IsSuccessful
        ? field
        : throw new InvalidOperationException($"Cannot get error message for successful call");
    
    /// <summary>
    ///     If the backend caught an exception during the execution of the agent, it will show up here.
    ///     Should never happen due to resource leaks that could happen.
    /// </summary>
    public Exception? ThrownException => !IsSuccessful
        ? field
        : throw new InvalidOperationException($"Cannot get exception for successful call");

    public string FormatError()
    {
        if (IsSuccessful)
        {
            throw new InvalidOperationException($"Cannot format error for successful call");
        }
        
        if (ErrorMessage == null && ThrownException == null)
        {
            return "Unspecified error";
        }

        if (ErrorMessage != null && ThrownException != null)
        {
            return $"{ErrorMessage}. Exception: {ThrownException.Message}";
        }

        if (ErrorMessage != null)
        {
            return ErrorMessage;
        }

        if (!string.IsNullOrEmpty(ThrownException!.Message))
        {
            return ThrownException.Message;
        }

        return ThrownException.ToString();
    }

    public override string ToString() => IsSuccessful 
        ? Output 
        : FormatError();
}
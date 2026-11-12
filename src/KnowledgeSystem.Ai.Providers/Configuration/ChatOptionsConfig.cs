using KnowledgeSystem.Ai.Providers.Abstractions;

namespace KnowledgeSystem.Ai.Providers.Configuration;

public sealed class ChatOptionsConfig
{
    public string? Model { get; set; }
    public float? Temperature { get; set; }
    public int? MaxOutputTokens { get; set; }
    public string? ReasoningEffort { get; set; }
    public string? ToolChoice { get; set; }

    public ChatRequestOptions CreateOptions()
    {
        return new ChatRequestOptions
        {
            Model = Model,
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ReasoningEffort = ReasoningEffort,
            ToolChoice = ToolChoice
        };
    }
}

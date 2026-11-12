namespace KnowledgeSystem.Ai.Providers.Abstractions;

public sealed class ChatProviderException : Exception
{
    public ChatProviderException(ChatError error) : base(error.Message)
    {
        Error = error;
    }

    public ChatError Error { get; }
}

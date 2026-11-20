using KnowledgeSystem.Ai.Providers.Abstractions;

namespace Subordinate.Core.Sessions;

/// <summary>
///     Removes the images of the messages a compaction keeps, so a resumed session sends the same text as the run that compacted.
/// </summary>
public static class CompactionImages
{
    public const string RemovedNote = "[image removed by context compaction]";

    public static ChatMessage StripImages(ChatMessage message)
    {
        List<ChatContent>? contents = null;

        for (var index = 0; index < message.Contents.Count; index++)
        {
            if (message.Contents[index] is not ImageContent)
            {
                continue;
            }

            contents ??= [.. message.Contents];
            contents[index] = new TextContent { Text = RemovedNote };
        }

        if (contents == null)
        {
            return message;
        }

        return new ChatMessage
        {
            Role = message.Role,
            Contents = contents,
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            ResponseId = message.ResponseId,
            ModelId = message.ModelId
        };
    }
}

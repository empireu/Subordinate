using System.Globalization;
using System.Runtime.CompilerServices;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Images;

namespace Subordinate.Core.Running;

/// <summary>
///     Replaces the image snapshots of a request with their base64 data URL, so every byte the model sees can be rebuilt from the session directory.
/// </summary>
public sealed class ImageResolvingChatProvider(IChatProvider inner, string imagesDirectory, Action<string> log) : IChatProvider
{
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public string ProviderId => inner.ProviderId;

    public string? DefaultModel => inner.DefaultModel;

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolved = Resolve(request);

        await foreach (var streamEvent in inner.StreamAsync(resolved, cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<ChatMessage> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        return inner.CompleteAsync(Resolve(request), cancellationToken);
    }

    private ChatRequest Resolve(ChatRequest request)
    {
        _used.Clear();
        var messages = new List<ChatMessage>(request.Messages.Count);

        foreach (var message in request.Messages)
        {
            messages.Add(ResolveMessage(message));
        }

        if (_cache.Count > 0)
        {
            foreach (var fileName in _cache.Keys.Where(name => !_used.Contains(name)).ToList())
            {
                _cache.Remove(fileName);
            }
        }

        return new ChatRequest
        {
            Model = request.Model,
            Messages = messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxOutputTokens,
            ReasoningEffort = request.ReasoningEffort,
            PromptCacheKey = request.PromptCacheKey,
            Metadata = request.Metadata
        };
    }

    private ChatMessage ResolveMessage(ChatMessage message)
    {
        List<ChatContent>? contents = null;

        for (var index = 0; index < message.Contents.Count; index++)
        {
            var content = message.Contents[index];

            if (content is not ImageContent image || !image.Url.StartsWith(ImageSnapshots.UrlPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            contents ??= [.. message.Contents];
            contents[index] = ResolveImage(image);
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

    private ChatContent ResolveImage(ImageContent image)
    {
        if (!ImageSnapshots.TryParse(image.Url, out var fileName))
        {
            return Unavailable(image.Url, "the reference is not a snapshot of this session");
        }

        if (_cache.TryGetValue(fileName, out var cached))
        {
            _used.Add(fileName);
            return new ImageContent { Url = cached, Detail = image.Detail };
        }

        var path = Path.Combine(imagesDirectory, fileName);
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Unavailable(fileName, $"the file could not be read: {ex.Message}");
        }

        var hash = ImageSnapshots.Hash(bytes);

        if (!string.Equals(hash, fileName[..ImageSnapshots.HashCharacters], StringComparison.Ordinal))
        {
            return Unavailable(fileName, "the contents no longer match the hash in its name");
        }

        var format = ImageFormat.FromExtension(Path.GetExtension(fileName)[1..]);

        if (format == null)
        {
            return Unavailable(fileName, "the file has an unknown extension");
        }

        var dataUrl = $"data:{format.MediaType};base64,{Convert.ToBase64String(bytes)}";
        _cache[fileName] = dataUrl;
        _used.Add(fileName);
        return new ImageContent { Url = dataUrl, Detail = image.Detail };
    }

    private TextContent Unavailable(string fileName, string reason)
    {
        if (_reported.Add(fileName))
        {
            log($"- [{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] image {fileName} is unavailable: {reason}");
        }

        return new TextContent
        {
            Text = $"[image unavailable: {fileName} could not be loaded from the session directory]"
        };
    }
}

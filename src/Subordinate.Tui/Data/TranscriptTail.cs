using System.Text;
using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Sessions;

namespace Subordinate.Tui.Data;

public sealed record TranscriptRefresh(IReadOnlyList<ChatMessage> Messages, bool Reset, string? Notice);

public sealed class TranscriptTail(string transcriptPath)
{
    private readonly List<ChatMessage> _messages = [];

    private DateTimeOffset _createdAt;
    private long _offset;
    private int _messagesRead;

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public TranscriptRefresh Refresh(SessionRecord record)
    {
        var pointer = record.Transcript;
        var reset = _createdAt != record.CreatedAt || pointer.Bytes < _offset || pointer.Count < _messagesRead;

        if (reset)
        {
            _createdAt = record.CreatedAt;
            _offset = 0;
            _messagesRead = 0;
            _messages.Clear();
        }

        if (pointer.Bytes <= _offset)
        {
            return new TranscriptRefresh([], reset, null);
        }

        var count = pointer.Bytes - _offset;

        if (count > int.MaxValue)
        {
            return new TranscriptRefresh([], reset, "The transcript is too large to read.");
        }

        var bytes = SharedFile.TryReadBytes(transcriptPath, _offset, (int)count);

        if (bytes == null)
        {
            return new TranscriptRefresh([], reset, "The transcript could not be read.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        var start = 0;
        var added = new List<ChatMessage>();
        string? notice = null;

        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);

            if (newline < 0)
            {
                break;
            }

            var line = text[start..newline];
            var lineStart = start;
            start = newline + 1;

            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                var message = TranscriptSerializer.Deserialize(line);
                _messages.Add(message);
                _messagesRead++;
                added.Add(message);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                notice = "The transcript contains a line that cannot be read.";
                start = lineStart;
                break;
            }
        }

        _offset += Encoding.UTF8.GetByteCount(text.AsSpan(0, start));
        return new TranscriptRefresh(added, reset, notice);
    }
}

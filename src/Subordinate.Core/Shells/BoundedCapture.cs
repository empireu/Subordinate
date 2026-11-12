using System.Text;

namespace Subordinate.Core.Shells;

/// <summary>
///     The captured output of one stream. Text holds the head and tail when the output was truncated.
/// </summary>
public sealed record CapturedOutput(string Text, long TotalCharacters, bool IsTruncated, string? FullOutputPath);

/// <summary>
///     Keeps the first and last characters of a stream in memory. When the stream outgrows both, everything is written to a file instead of being dropped.
/// </summary>
public sealed class BoundedCapture : IDisposable
{
    private readonly int _headLimit;
    private readonly char[] _tail;
    private readonly string _spillPath;
    private readonly StringBuilder _head = new();
    private int _tailStart;
    private int _tailCount;
    private long _total;
    private StreamWriter? _spill;

    public BoundedCapture(int headLimit, int tailLimit, string spillPath)
    {
        _headLimit = headLimit;
        _tail = new char[Math.Max(1, tailLimit)];
        _spillPath = spillPath;
    }

    public async Task ReadToEndAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);

            if (read == 0)
            {
                break;
            }

            Append(buffer.AsSpan(0, read));
        }
    }

    public void Append(ReadOnlySpan<char> chunk)
    {
        _total += chunk.Length;

        var headRoom = _headLimit - _head.Length;
        if (headRoom > 0)
        {
            var taken = Math.Min(headRoom, chunk.Length);
            _head.Append(chunk[..taken]);
            chunk = chunk[taken..];
        }

        if (chunk.IsEmpty)
        {
            return;
        }

        if (_spill == null && _tailCount + chunk.Length > _tail.Length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_spillPath)!);
            _spill = new StreamWriter(_spillPath, false, new UTF8Encoding(false));
            _spill.Write(_head.ToString());
            _spill.Write(TailText());
        }

        _spill?.Write(chunk);

        foreach (var character in chunk)
        {
            if (_tailCount < _tail.Length)
            {
                _tail[(_tailStart + _tailCount) % _tail.Length] = character;
                _tailCount++;
            }
            else
            {
                _tail[_tailStart] = character;
                _tailStart = (_tailStart + 1) % _tail.Length;
            }
        }
    }

    public CapturedOutput Complete()
    {
        _spill?.Flush();
        _spill?.Dispose();
        var spilled = _spill != null;
        _spill = null;

        if (!spilled)
        {
            return new CapturedOutput(_head + TailText(), _total, false, null);
        }

        var omitted = _total - _head.Length - _tailCount;
        var text = $"{_head}\n... [{omitted} characters omitted] ...\n{TailText()}";
        return new CapturedOutput(text, _total, true, _spillPath);
    }

    private string TailText()
    {
        var builder = new StringBuilder(_tailCount);

        for (var index = 0; index < _tailCount; index++)
        {
            builder.Append(_tail[(_tailStart + index) % _tail.Length]);
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        _spill?.Dispose();
    }
}

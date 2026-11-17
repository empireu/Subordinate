using System.Text;
using Subordinate.Core.Monitoring;

namespace Subordinate.Tui.Live;

public sealed class BoundedLineReader(Stream stream, int maxLineBytes = MonitorProtocol.MaxLineBytes)
{
    private readonly byte[] _buffer = new byte[4096];
    private readonly List<byte> _line = [];

    private int _start;
    private int _end;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            for (var index = _start; index < _end; index++)
            {
                if (_buffer[index] == (byte)'\n')
                {
                    Append(_start, index);
                    _start = index + 1;
                    return CompleteLine();
                }
            }

            if (_end > _start)
            {
                Append(_start, _end);
                _start = 0;
                _end = 0;
            }

            if (_line.Count > maxLineBytes)
            {
                _line.Clear();
                throw new InvalidDataException("The monitor line is longer than the limit.");
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken);

            if (read == 0)
            {
                _line.Clear();
                return null;
            }

            _start = 0;
            _end = read;
        }
    }

    private void Append(int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            _line.Add(_buffer[index]);
        }
    }

    private string CompleteLine()
    {
        if (_line.Count > maxLineBytes)
        {
            _line.Clear();
            throw new InvalidDataException("The monitor line is longer than the limit.");
        }

        var count = _line.Count;

        if (count > 0 && _line[count - 1] == (byte)'\r')
        {
            count--;
        }

        var text = Encoding.UTF8.GetString(_line.ToArray(), 0, count);
        _line.Clear();
        return text;
    }
}

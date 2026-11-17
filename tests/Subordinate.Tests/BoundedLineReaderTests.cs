using System.Text;
using Subordinate.Tui.Live;

namespace Subordinate.Tests;

public class BoundedLineReaderTests
{
    [Fact]
    public async Task ReadsLinesAcrossChunkBoundaries()
    {
        var stream = new ChunkedStream(Encoding.UTF8.GetBytes("alpha\nbeta\r\n\ngamma"), 2);
        var reader = new BoundedLineReader(stream);

        Assert.Equal("alpha", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("beta", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsALineOfExactlyTheLimit()
    {
        var line = new string('a', 100);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));
        var reader = new BoundedLineReader(stream, 100);

        Assert.Equal(line, await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsALineOneByteOverTheLimit()
    {
        var line = new string('a', 101);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));
        var reader = new BoundedLineReader(stream, 100);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DiscardsAPartialLineAtEndOfStream()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("complete\npartial"));
        var reader = new BoundedLineReader(stream);

        Assert.Equal("complete", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(Math.Min(count, chunkSize), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var available = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
            data.AsSpan(_position, available).CopyTo(buffer.Span);
            _position += available;
            return ValueTask.FromResult(available);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}

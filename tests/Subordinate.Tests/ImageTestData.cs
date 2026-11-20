using System.IO.Compression;
using System.Text;

namespace Subordinate.Tests;

/// <summary>
///     Builds small images in code, so the repository holds no binary test files.
/// </summary>
internal static class ImageTestData
{
    public static byte[] SolidRedPng16()
    {
        return SolidPng(16, 16, 255, 0, 0);
    }

    public static byte[] SolidPng(int width, int height, byte red, byte green, byte blue)
    {
        var raw = new byte[height * (1 + (width * 3))];
        var index = 0;

        for (var row = 0; row < height; row++)
        {
            // PNG stores one filter byte at the start of every scanline:
            raw[index++] = 0;

            for (var column = 0; column < width; column++)
            {
                raw[index++] = red;
                raw[index++] = green;
                raw[index++] = blue;
            }
        }

        using var compressed = new MemoryStream();

        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;
        header[9] = 2;

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    public static byte[] JpegHeader()
    {
        return [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02];
    }

    public static byte[] GifHeader()
    {
        return [.. Encoding.ASCII.GetBytes("GIF89a"), 0x10, 0x00, 0x10, 0x00, 0x00, 0x00];
    }

    public static byte[] WebpHeader()
    {
        return [.. Encoding.ASCII.GetBytes("RIFF"), 0x1A, 0x00, 0x00, 0x00, .. Encoding.ASCII.GetBytes("WEBPVP8 "), 0x00];
    }

    public static string NewTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void DeleteTemporaryDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);

        var crc = new byte[4];
        WriteBigEndian(crc, 0, ComputeCrc32(crcInput));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

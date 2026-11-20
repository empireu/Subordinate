using System.IO.Compression;
using System.Text;
using Subordinate.Cli;
using Subordinate.Core.Images;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

[Collection("cli")]
public class ImageSnapshotTests
{    private static readonly string ValidHash = new('a', ImageSnapshots.HashCharacters);

    [Fact]
    public void DetectRecognizesEachFormat()
    {
        Assert.Same(ImageFormat.Png, ImageSnapshots.Detect(ImageTestData.SolidRedPng16()));
        Assert.Same(ImageFormat.Jpeg, ImageSnapshots.Detect(ImageTestData.JpegHeader()));
        Assert.Same(ImageFormat.Gif, ImageSnapshots.Detect(ImageTestData.GifHeader()));
        Assert.Same(ImageFormat.Gif, ImageSnapshots.Detect(Encoding.ASCII.GetBytes("GIF87a")));
        Assert.Same(ImageFormat.Webp, ImageSnapshots.Detect(ImageTestData.WebpHeader()));
    }

    [Fact]
    public void DetectRejectsTextAndEmptySpans()
    {
        Assert.Null(ImageSnapshots.Detect([]));
        Assert.Null(ImageSnapshots.Detect(Encoding.ASCII.GetBytes("hello, this is a text file.")));
        Assert.Null(ImageSnapshots.Detect(Encoding.ASCII.GetBytes("GIF8")));
    }

    [Fact]
    public void DetectRejectsSpansShorterThanTheirHeader()
    {
        Assert.Null(ImageSnapshots.Detect(ImageTestData.SolidRedPng16()[..7]));
        Assert.Null(ImageSnapshots.Detect(ImageTestData.JpegHeader()[..2]));
        Assert.Null(ImageSnapshots.Detect(ImageTestData.GifHeader()[..5]));
        Assert.Null(ImageSnapshots.Detect(ImageTestData.WebpHeader()[..11]));
    }

    [Fact]
    public void SaveWritesTheHashAndTheExtension()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var bytes = ImageTestData.SolidRedPng16();

            var reference = ImageSnapshots.Save(directory, bytes, ImageFormat.Png);

            Assert.True(ImageSnapshots.TryParse(reference, out var fileName));
            Assert.Equal(ImageSnapshots.Hash(bytes) + ".png", fileName);
            Assert.True(File.Exists(Path.Combine(directory, fileName)));
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void SaveUsesTheExtensionOfTheDetectedFormat()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var reference = ImageSnapshots.Save(directory, ImageTestData.WebpHeader(), ImageFormat.Webp);

            Assert.EndsWith(".webp", reference, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(directory, reference[ImageSnapshots.UrlPrefix.Length..])));
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void SaveSkipsAFileThatAlreadyExists()
    {
        var directory = ImageTestData.NewTemporaryDirectory();

        try
        {
            var bytes = ImageTestData.SolidRedPng16();
            var reference = ImageSnapshots.Save(directory, bytes, ImageFormat.Png);
            var path = Path.Combine(directory, reference[ImageSnapshots.UrlPrefix.Length..]);
            var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);

            var second = ImageSnapshots.Save(directory, bytes, ImageFormat.Png);

            Assert.Equal(reference, second);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            ImageTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void TryParseAcceptsAValidReference()
    {
        Assert.True(ImageSnapshots.TryParse(ImageSnapshots.UrlPrefix + ValidHash + ".png", out var fileName));
        Assert.Equal(ValidHash + ".png", fileName);
        Assert.True(ImageSnapshots.TryParse(ImageSnapshots.UrlPrefix + ValidHash + ".webp", out _));
    }

    [Theory]
    [InlineData("subordinate-image:../x.png")]
    [InlineData("subordinate-image:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.png")]
    [InlineData("subordinate-image:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png")]
    [InlineData("subordinate-image:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa..png")]
    [InlineData("subordinate-image:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png/../x")]
    [InlineData("subordinate-image:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bmp")]
    [InlineData("subordinate-image:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("")]
    public void TryParseRejectsAnythingElse(string url)
    {
        Assert.False(ImageSnapshots.TryParse(url, out var fileName));
        Assert.Equal(string.Empty, fileName);
    }

    [Fact]
    public void TryParseRejectsAPathSeparatorInTheName()
    {
        Assert.False(ImageSnapshots.TryParse("subordinate-image:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\.png", out _));
    }

    [Fact]
    public void TheProbeImageIsAValidSixteenPixelPng()
    {
        var bytes = ImageTestData.SolidRedPng16();

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], bytes[..8]);
        Assert.Equal(16, ReadBigEndian(bytes, 16));
        Assert.Equal(16, ReadBigEndian(bytes, 20));
        Assert.Equal(8, bytes[24]);
        Assert.Equal(2, bytes[25]);

        using var compressed = new MemoryStream(ReadChunkData(bytes, "IDAT"));
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        var scanlines = raw.ToArray();

        Assert.Equal(16 * (1 + (16 * 3)), scanlines.Length);

        for (var row = 0; row < 16; row++)
        {
            var offset = row * (1 + (16 * 3));
            Assert.Equal(0, scanlines[offset]);

            for (var column = 0; column < 16; column++)
            {
                Assert.Equal(255, scanlines[offset + 1 + (column * 3)]);
                Assert.Equal(0, scanlines[offset + 2 + (column * 3)]);
                Assert.Equal(0, scanlines[offset + 3 + (column * 3)]);
            }
        }
    }

    [Fact]
    public void DeletingASessionRemovesItsImages()
    {
        using var harness = new CliHarness();
        var store = new SessionStore(harness.Home);
        var workdir = harness.CreateWorkdir();
        harness.CreateSession(store, "image-delete", workdir, SessionStates.Finished);
        var paths = store.PathsFor(workdir, "image-delete");
        var reference = ImageSnapshots.Save(paths.ImagesDirectory, ImageTestData.SolidRedPng16(), ImageFormat.Png);
        Assert.True(File.Exists(Path.Combine(paths.ImagesDirectory, reference[ImageSnapshots.UrlPrefix.Length..])));

        var commandLine = CommandLine.Parse(["delete", "--name", "image-delete"], new HashSet<string> { "name", "workdir" }, new HashSet<string>());
        SessionCommands.Delete(commandLine);

        Assert.False(Directory.Exists(paths.Directory));
        Assert.False(Directory.Exists(paths.ImagesDirectory));
    }

    private static int ReadBigEndian(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
    }

    private static byte[] ReadChunkData(byte[] png, string type)
    {
        var offset = 8;

        while (offset + 8 <= png.Length)
        {
            var length = ReadBigEndian(png, offset);
            var chunkType = Encoding.ASCII.GetString(png, offset + 4, 4);

            if (chunkType == type)
            {
                return png[(offset + 8)..(offset + 8 + length)];
            }

            offset += 12 + length;
        }

        throw new InvalidOperationException($"The image has no {type} chunk.");
    }
}

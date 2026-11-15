using Subordinate.Core.Tools;

namespace Subordinate.Tests;

public class FileToolLineEndingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));

    public FileToolLineEndingTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void CrlfFileSingleLineEditKeepsCrlf()
    {
        var error = EditFileToolHandler.TryEdit("test.txt", "alpha\r\nbeta\r\ngamma\r\n", "beta", "BETA", false, out var updated, out var count);

        Assert.Null(error);
        Assert.Equal(1, count);
        Assert.Equal("alpha\r\nBETA\r\ngamma\r\n", updated);
    }

    [Fact]
    public void CrlfFileMatchesMultiLineOldStringWithLineFeeds()
    {
        var error = EditFileToolHandler.TryEdit("test.txt", "alpha\r\nbeta\r\ngamma\r\n", "alpha\nbeta", "alpha\nBETA", false, out var updated, out var count);

        Assert.Null(error);
        Assert.Equal(1, count);
        Assert.Equal("alpha\r\nBETA\r\ngamma\r\n", updated);
    }

    [Fact]
    public void CrlfFileConvertsMultiLineNewString()
    {
        var error = EditFileToolHandler.TryEdit("test.txt", "alpha\r\nbeta\r\n", "beta", "one\ntwo\nthree", false, out var updated, out _);

        Assert.Null(error);
        Assert.Equal("alpha\r\none\r\ntwo\r\nthree\r\n", updated);
        Assert.DoesNotContain("\n", updated.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void CrlfFileStillAcceptsCrlfOldString()
    {
        var error = EditFileToolHandler.TryEdit("test.txt", "alpha\r\nbeta\r\n", "alpha\r\nbeta", "alpha\r\nBETA", false, out var updated, out _);

        Assert.Null(error);
        Assert.Equal("alpha\r\nBETA\r\n", updated);
    }

    [Fact]
    public void LfFileBehavesAsBefore()
    {
        var error = EditFileToolHandler.TryEdit("test.txt", "alpha\nbeta\ngamma\n", "beta", "one\ntwo", false, out var updated, out var count);

        Assert.Null(error);
        Assert.Equal(1, count);
        Assert.Equal("alpha\none\ntwo\ngamma\n", updated);
        Assert.DoesNotContain("\r", updated);
    }

    [Fact]
    public void ReplaceAllCountsOccurrencesInCrlfFile()
    {
        var content = "a\r\nb\r\na\r\nb\r\nc\r\n";

        var error = EditFileToolHandler.TryEdit("test.txt", content, "a\nb", "x", true, out var updated, out var count);

        Assert.Null(error);
        Assert.Equal(2, count);
        Assert.Equal("x\r\nx\r\nc\r\n", updated);
    }

    [Fact]
    public void AmbiguousMultiLineMatchInCrlfFileIsRejectedWithoutReplaceAll()
    {
        var error = EditFileToolHandler.TryEdit("test.txt", "a\r\nb\r\na\r\nb\r\n", "a\nb", "x", false, out var updated, out var count);

        Assert.NotNull(error);
        Assert.Contains("2 times", error);
        Assert.Equal(2, count);
        Assert.Equal("a\r\nb\r\na\r\nb\r\n", updated);
    }

    [Fact]
    public void MissingTextStillReportsNotFound()
    {
        var error = EditFileToolHandler.TryEdit("test.txt", "alpha\r\nbeta\r\n", "alpha\ngamma", "x", false, out _, out var count);

        Assert.NotNull(error);
        Assert.Contains("not found", error);
        Assert.Equal(0, count);
    }

    [Fact]
    public void MixedFileWithMajorityCrlfWritesCrlfWithoutCorruptingExistingLines()
    {
        var content = "one\r\ntwo\r\nthree\nfour\r\n";

        var error = EditFileToolHandler.TryEdit("test.txt", content, "two", "2a\n2b", false, out var updated, out _);

        Assert.Null(error);
        Assert.Equal("one\r\n2a\r\n2b\r\nthree\nfour\r\n", updated);
    }

    [Fact]
    public void MixedFileWithMajorityLfKeepsLfForNewText()
    {
        var content = "one\ntwo\nthree\r\nfour\n";

        var error = EditFileToolHandler.TryEdit("test.txt", content, "two", "2a\n2b", false, out var updated, out _);

        Assert.Null(error);
        Assert.Equal("one\n2a\n2b\nthree\r\nfour\n", updated);
    }

    [Fact]
    public void MixedFileMatchesMultiLineTextAcrossCrlfLines()
    {
        var content = "one\ntwo\r\nthree\r\nfour\n";

        var error = EditFileToolHandler.TryEdit("test.txt", content, "two\nthree", "2\n3", false, out var updated, out _);

        Assert.Null(error);
        Assert.Equal("one\n2\r\n3\r\nfour\n", updated);
    }

    [Fact]
    public void ByteOrderMarkIsReportedAndExcludedFromContent()
    {
        var path = Path.Combine(_directory, "bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "alpha\r\nbeta\r\n"u8.ToArray()]);

        var content = FileToolPaths.ReadText(path, out var isBinary, out var hasByteOrderMark);
        var error = EditFileToolHandler.TryEdit(path, content, "alpha\nbeta", "alpha\nBETA", false, out var updated, out _);

        Assert.False(isBinary);
        Assert.True(hasByteOrderMark);
        Assert.Null(error);
        Assert.Equal("alpha\r\nBETA\r\n", updated);
    }

    [Fact]
    public void WriteOverCrlfFileKeepsCrlf()
    {
        var path = Path.Combine(_directory, "existing.txt");
        File.WriteAllText(path, "alpha\r\nbeta\r\n");

        Assert.Equal("one\r\ntwo\r\n", FileToolPaths.MatchExistingLineEndings(path, "one\ntwo\n"));
    }

    [Fact]
    public void WriteOverLfFileLeavesContentAlone()
    {
        var path = Path.Combine(_directory, "existing.txt");
        File.WriteAllText(path, "alpha\nbeta\n");

        Assert.Equal("one\ntwo\n", FileToolPaths.MatchExistingLineEndings(path, "one\ntwo\n"));
    }

    [Fact]
    public void WriteToNewFileLeavesContentAlone()
    {
        var path = Path.Combine(_directory, "missing.txt");

        Assert.Equal("one\ntwo\n", FileToolPaths.MatchExistingLineEndings(path, "one\ntwo\n"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

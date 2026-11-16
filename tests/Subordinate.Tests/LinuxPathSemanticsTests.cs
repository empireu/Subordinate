using Subordinate.Core.Classification;
using Subordinate.Core.Platform;

namespace Subordinate.Tests;

public class LinuxPathSemanticsTests
{
    [Theory]
    [InlineData("/work/repo/src/a.cs", null, ResolvedPathKind.Local, "/work/repo/src/a.cs")]
    [InlineData("src/../b.txt", "/work/repo", ResolvedPathKind.Local, "/work/repo/b.txt")]
    [InlineData("../other", "/work/repo", ResolvedPathKind.Local, "/work/other")]
    [InlineData("./a//b/./c/", "/work/repo", ResolvedPathKind.Local, "/work/repo/a/b/c")]
    [InlineData("/", null, ResolvedPathKind.Local, "/")]
    [InlineData("/..", null, ResolvedPathKind.Local, "/")]
    [InlineData("//server/share/x", null, ResolvedPathKind.Local, "/server/share/x")]
    [InlineData("/c/Users/me/x", null, ResolvedPathKind.Local, "/c/Users/me/x")]
    [InlineData("D:/repos/y", "/work/repo", ResolvedPathKind.Local, "/work/repo/D:/repos/y")]
    [InlineData("C:relative", "/work/repo", ResolvedPathKind.Local, "/work/repo/C:relative")]
    [InlineData("nul", "/work/repo", ResolvedPathKind.Local, "/work/repo/nul")]
    [InlineData("/dev/null", "/work/repo", ResolvedPathKind.NullDevice, null)]
    [InlineData("$null", "/work/repo", ResolvedPathKind.NullDevice, null)]
    [InlineData("src\\a.cs", "/work/repo", ResolvedPathKind.Unknown, null)]
    [InlineData("..\\..\\etc", "/work/repo", ResolvedPathKind.Unknown, null)]
    [InlineData("~/x", "/work/repo", ResolvedPathKind.Unknown, null)]
    [InlineData("relative", null, ResolvedPathKind.Unknown, null)]
    [InlineData("/tmp/a*b", null, ResolvedPathKind.Unknown, null)]
    [InlineData("env:SECRET", "/work/repo", ResolvedPathKind.Foreign, null)]
    [InlineData("https://example.invalid/a", "/work/repo", ResolvedPathKind.Foreign, null)]
    public void ResolvesLinuxPaths(string value, string? baseDirectory, ResolvedPathKind kind, string? expected)
    {
        var resolved = LinuxPathSemantics.Instance.Resolve(value, baseDirectory);

        Assert.Equal(kind, resolved.Kind);
        Assert.Equal(expected, resolved.Path);
    }

    [Theory]
    [InlineData("/work/repo", "/work/repo", true)]
    [InlineData("/work/repo/src", "/work/repo", true)]
    [InlineData("/work/REPO/src", "/work/repo", false)]
    [InlineData("/work/repo2", "/work/repo", false)]
    [InlineData("/work", "/work/repo", false)]
    [InlineData("/anything", "/", true)]
    public void ContainmentIsLexicalAndCaseSensitive(string path, string root, bool inside)
    {
        Assert.Equal(inside, LinuxPathSemantics.IsInsideLexically(path, root));
    }
}

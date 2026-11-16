using Subordinate.Core.Classification;
using Subordinate.Core.Json;
using Subordinate.Core.Policy;

using Subordinate.Core.Platform;

namespace Subordinate.Tests;

public class PathAndPolicyTests
{
    [WindowsTheory]
    [InlineData("/d/repos/x/src/a.cs", null, ResolvedPathKind.Local, @"D:\repos\x\src\a.cs")]
    [InlineData("D:/repos/y/c.txt", null, ResolvedPathKind.Local, @"D:\repos\y\c.txt")]
    [InlineData(@"D:\repos\y\c.txt", null, ResolvedPathKind.Local, @"D:\repos\y\c.txt")]
    [InlineData("src/../b.txt", @"D:\repos\x", ResolvedPathKind.Local, @"D:\repos\x\b.txt")]
    [InlineData("../other", @"D:\repos\x", ResolvedPathKind.Local, @"D:\repos\other")]
    [InlineData("/d", null, ResolvedPathKind.Local, @"D:\")]
    [InlineData("/tmp/x", @"D:\repos\x", ResolvedPathKind.Foreign, null)]
    [InlineData("//server/share/x", @"D:\repos\x", ResolvedPathKind.Foreign, null)]
    [InlineData("env:SECRET", @"D:\repos\x", ResolvedPathKind.Foreign, null)]
    [InlineData("https://example.invalid/a", @"D:\repos\x", ResolvedPathKind.Foreign, null)]
    [InlineData("/dev/null", @"D:\repos\x", ResolvedPathKind.NullDevice, null)]
    [InlineData("NUL", @"D:\repos\x", ResolvedPathKind.NullDevice, null)]
    [InlineData("~/x", @"D:\repos\x", ResolvedPathKind.Unknown, null)]
    [InlineData("relative", null, ResolvedPathKind.Unknown, null)]
    [InlineData("C:relative", @"D:\repos\x", ResolvedPathKind.Unknown, null)]
    public void ResolvesShellPaths(string value, string? baseDirectory, ResolvedPathKind kind, string? expected)
    {
        var resolved = ShellPaths.Resolve(value, baseDirectory);

        Assert.Equal(kind, resolved.Kind);
        Assert.Equal(expected, resolved.Path);
    }

    [WindowsTheory]
    [InlineData(@"D:\repos\x", @"D:\repos\x", true)]
    [InlineData(@"d:\Repos\X\src", @"D:\repos\x", true)]
    [InlineData(@"D:\repos\x2", @"D:\repos\x", false)]
    [InlineData(@"D:\repos", @"D:\repos\x", false)]
    public void ContainmentIsSegmentAwareAndCaseInsensitive(string path, string root, bool inside)
    {
        Assert.Equal(inside, ShellPaths.IsInside(path, root));
    }

    [WindowsFact]
    public void ConvertsWindowsPathToGitBashForm()
    {
        Assert.Equal("/d/repos/x", WindowsPathSemantics.ToBashPath(@"D:\repos\x\"));
        Assert.Null(WindowsPathSemantics.ToBashPath(@"\\server\share"));
    }

    [Fact]
    public void ParsesPresetsCapabilitiesAndCommandGrants()
    {
        Assert.True(PolicySet.TryParse(["build", "cmd:npm   run Lint , network"], out var policy, out var error), error);

        Assert.Equal(["read", "exec", "network", "cmd:npm run lint"], policy!.Tokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("readonly,teleport")]
    [InlineData("cmd:")]
    [InlineData("dangerous")]
    [InlineData("sensitive")]
    public void RejectsInvalidPolicies(string text)
    {
        Assert.False(PolicySet.TryParse([text], out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void StoredPolicyRoundTrips()
    {
        Assert.True(PolicySet.TryParse(["edit,cmd:make test"], out var policy, out _));

        var restored = PolicySet.FromTokens(policy!.Tokens);

        Assert.Equal(policy.Tokens, restored.Tokens);
    }

    [Fact]
    public void JsonEnvelopeIsAsciiAndKeepsReadablePunctuation()
    {
        var json = AsciiJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("b", "x \u2248 y \u2014 <a> & 'q'");
            writer.WriteString("a", "line\nbreak");
            writer.WriteEndObject();
        }, false);

        Assert.All(json, character => Assert.InRange(character, ' ', '~'));
        Assert.Equal("{\"b\":\"x \\u2248 y \\u2014 <a> & 'q'\",\"a\":\"line\\nbreak\"}", json);
    }

    [Fact]
    public void SensitivePathDetectionIgnoresSettingsFiles()
    {
        Assert.Empty(SensitivePaths.FindMatches("cat ~/.aws/config ~/.gitconfig .git/config .env.example"));
        Assert.NotEmpty(SensitivePaths.FindMatches(@"type C:\Users\me\.ssh\id_ed25519"));
        Assert.NotEmpty(SensitivePaths.FindMatches("cat .netrc"));
        Assert.NotEmpty(SensitivePaths.FindMatches("cat secrets/.pgpass"));
    }

    [Fact]
    public void SensitivePathDetectionIncludesProcessEnvironments()
    {
        Assert.NotEmpty(SensitivePaths.FindMatches("cat /proc/1234/environ"));
        Assert.NotEmpty(SensitivePaths.FindMatches("cat /proc/self/environ"));
        Assert.Empty(SensitivePaths.FindMatches("cat /proc/cpuinfo"));
    }

    [Fact]
    public void SensitivePathDetectionIncludesLinuxCredentialStores()
    {
        Assert.NotEmpty(SensitivePaths.FindMatches("cat ~/.password-store/x.gpg"));
        Assert.NotEmpty(SensitivePaths.FindMatches("ls ~/.local/share/keyrings"));
        Assert.NotEmpty(SensitivePaths.FindMatches("cat /etc/shadow"));
        Assert.NotEmpty(SensitivePaths.FindMatches("cat /etc/gshadow"));
    }
}

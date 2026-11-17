using Subordinate.Tui.Presentation;

namespace Subordinate.Tests;

public class TerminalTextTests
{
    [Fact]
    public void EscapesControlCharacters()
    {
        Assert.Equal("a^[[31mb", TerminalText.Sanitize("a\u001b[31mb"));
        Assert.Equal("^G", TerminalText.Sanitize("\u0007"));
        Assert.Equal("^?", TerminalText.Sanitize("\u007f"));
        Assert.Equal("?", TerminalText.Sanitize("\u009b"));
        Assert.Equal("?", TerminalText.Sanitize("\u202e"));
    }

    [Fact]
    public void NormalizesLineEndings()
    {
        Assert.Equal("x\ny\nz", TerminalText.Sanitize("x\r\ny\rz"));
    }

    [Fact]
    public void KeepsTabsAndNewlines()
    {
        Assert.Equal("a\tb\nc", TerminalText.Sanitize("a\tb\nc"));
    }

    [Fact]
    public void ReplacesLoneSurrogates()
    {
        Assert.Equal("?", TerminalText.Sanitize("\ud800"));
        Assert.Equal("\ud83d\ude00", TerminalText.Sanitize("\ud83d\ude00"));
    }
}

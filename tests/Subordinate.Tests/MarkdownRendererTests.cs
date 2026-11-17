using System.Text;
using Subordinate.Tui.Presentation;

namespace Subordinate.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void PlainTextHasNoStyles()
    {
        var lines = MarkdownRenderer.Render("Just text.", 40, false);

        Assert.Equal([new StyledLine("Just text.", null)], lines);
    }

    [Fact]
    public void InlineMarkupIsStyledAndItsMarkersRemoved()
    {
        var line = Assert.Single(MarkdownRenderer.Render("Use **bold**, *slanted*, ~~gone~~ and `code`.", 80, false));

        Assert.Equal("Use bold, slanted, gone and code.", line.Text);
        Assert.Equal(MarkdownStyle.None, StyleOf(line, "Use"));
        Assert.Equal(MarkdownStyle.Bold, StyleOf(line, "bold"));
        Assert.Equal(MarkdownStyle.Italic, StyleOf(line, "slanted"));
        Assert.Equal(MarkdownStyle.Strikethrough, StyleOf(line, "gone"));
        Assert.Equal(MarkdownStyle.Code, StyleOf(line, "code"));
    }

    [Fact]
    public void PlainOutputKeepsHeadingMarksBackticksAndFences()
    {
        var lines = MarkdownRenderer.Render("# Title\n\nRun `make`.\n\n```sh\nmake all\n```", 40, true);

        Assert.Equal(["# Title", string.Empty, "Run `make`.", string.Empty, "```sh", "make all", "```"], Texts(lines));
    }

    [Fact]
    public void HeadingsAreStyledWithoutTheirMarks()
    {
        var lines = MarkdownRenderer.Render("## Findings\n\nText.", 40, false);

        Assert.Equal(["Findings", string.Empty, "Text."], Texts(lines));
        Assert.Equal(MarkdownStyle.Heading, StyleOf(lines[0], "Findings"));
    }

    [Fact]
    public void SoftLineBreaksAreKept()
    {
        var lines = MarkdownRenderer.Render("line one\nline two", 40, false);

        Assert.Equal(["line one", "line two"], Texts(lines));
    }

    [Fact]
    public void ListsAreIndentedAndNumbersAligned()
    {
        var bullets = MarkdownRenderer.Render("- one\n- two\n  - nested", 40, false);
        var numbered = MarkdownRenderer.Render(string.Join("\n", Enumerable.Range(1, 10).Select(index => $"{index}. item")), 40, false);

        Assert.Equal(["- one", "- two", "  - nested"], Texts(bullets));
        Assert.Equal(MarkdownStyle.Marker, StyleOf(bullets[0], "-"));
        Assert.Equal(" 1. item", numbered[0].Text);
        Assert.Equal("10. item", numbered[9].Text);
    }

    [Fact]
    public void WrappedListItemsHangUnderTheirText()
    {
        var lines = MarkdownRenderer.Render("- alpha beta gamma delta epsilon zeta", 16, false);

        Assert.Equal(["- alpha beta", "  gamma delta", "  epsilon zeta"], Texts(lines));
    }

    [Fact]
    public void TaskListsShowTheirCheckboxes()
    {
        var lines = MarkdownRenderer.Render("- [x] done\n- [ ] todo", 40, false);

        Assert.Equal(["- [x] done", "- [ ] todo"], Texts(lines));
    }

    [Fact]
    public void CodeBlocksArePaddedPanelsWhenStyled()
    {
        var lines = MarkdownRenderer.Render("```cs\nif (a)\n\treturn;\n```", 20, false);

        Assert.Equal([" if (a)", "     return;"], lines.Select(line => line.Text.TrimEnd()));
        Assert.All(lines, line => Assert.Equal(20, line.Text.Length));
        Assert.All(lines, line => Assert.All(line.Styles!, style => Assert.Equal((char)MarkdownStyle.CodeBlock, style)));
    }

    [Fact]
    public void QuotesGetABar()
    {
        Assert.Equal(["| quoted", "|", "| more"], Texts(MarkdownRenderer.Render("> quoted\n>\n> more", 40, false)));
        Assert.Equal(["> quoted"], Texts(MarkdownRenderer.Render("> quoted", 40, true)));
    }

    [Fact]
    public void ThematicBreaksSpanTheWidth()
    {
        var lines = MarkdownRenderer.Render("a\n\n---\n\nb", 12, false);

        Assert.Equal(["a", string.Empty, new string('-', 12), string.Empty, "b"], Texts(lines));
    }

    [Fact]
    public void LinksShowTheirTarget()
    {
        var line = Assert.Single(MarkdownRenderer.Render("See [the docs](https://example.com/docs).", 80, false));

        Assert.Equal("See the docs (https://example.com/docs).", line.Text);
        Assert.Equal(MarkdownStyle.Link, StyleOf(line, "the docs"));
    }

    [Fact]
    public void TablesAreAligned()
    {
        var lines = MarkdownRenderer.Render("| a | bb |\n|---|--:|\n| ccc | d |", 40, false);

        Assert.Equal(["a   | bb", "----+---", "ccc |  d"], Texts(lines));
        Assert.Equal(MarkdownStyle.Bold, StyleOf(lines[0], "bb"));
    }

    [Fact]
    public void WideTablesWrapTheirCellsToFit()
    {
        var text = "| name | description |\n|---|---|\n| parser | " + string.Join(" ", Enumerable.Repeat("words", 30)) + " |";

        var lines = MarkdownRenderer.Render(text, 40, false);

        Assert.True(lines.Count > 4, "The long cell should wrap onto several lines.");
        Assert.All(lines, line => Assert.True(line.Text.Length <= 40, $"\"{line.Text}\" is wider than 40."));
        Assert.All(lines.Skip(2), line => Assert.Equal(7, line.Text.IndexOf('|')));
    }

    [Fact]
    public void LinesFitTheWidth()
    {
        var text = "# A heading that is rather long for the width\n\n> - a quoted list item with " + string.Join(" ", Enumerable.Repeat("text", 20)) + "\n\n```\n" + new string('x', 100) + "\n```";

        foreach (var width in new[] { 10, 23, 40 })
        {
            Assert.All(MarkdownRenderer.Render(text, width, false), line => Assert.True(line.Text.Length <= width, $"\"{line.Text}\" is wider than {width}."));
        }
    }

    [Fact]
    public void PathologicalInputDoesNotThrow()
    {
        var deepQuote = new string('>', 500) + " deep";
        var manyStars = new string('*', 5000) + "text" + new string('*', 5000);
        var deepList = string.Join("\n", Enumerable.Range(0, 200).Select(index => new string(' ', index * 2) + "- item"));

        foreach (var text in new[] { deepQuote, manyStars, deepList, "| a |\n|---|", "```", "[x](", "\t\tcode" })
        {
            Assert.NotNull(MarkdownRenderer.Render(text, 40, false));
        }

        Assert.Contains(MarkdownRenderer.Render(deepQuote, 40, false), line => line.Text.Contains("deep", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(38)]
    [InlineData(78)]
    [InlineData(118)]
    public void TheLiveTailMatchesRenderingTheWholeText(int width)
    {
        var random = new Random(42);
        var words = new[] { "the", "model", "considers", "a", "reasonably", "long", "sentence", "about", "`builds`", "and", "**tests**" };
        var builder = new StringBuilder();

        while (builder.Length < 30_000)
        {
            switch (random.Next(6))
            {
                case 0:
                {
                    builder.Append("## Heading ").Append(random.Next(100)).Append("\n\n");
                    break;
                }
                case 1:
                {
                    builder.Append("- first item\n- second item\n\n");
                    break;
                }
                case 2:
                {
                    builder.Append("```\ncode line\n\n");

                    for (var line = 0; line < random.Next(1, 30); line++)
                    {
                        builder.Append("more code ").Append(line).Append('\n');
                    }

                    builder.Append("```\n\n");
                    break;
                }
                default:
                {
                    for (var word = 0; word < random.Next(5, 80); word++)
                    {
                        builder.Append(words[random.Next(words.Length)]).Append(' ');
                    }

                    builder.Append("\n\n");
                    break;
                }
            }
        }

        var text = builder.ToString();

        for (var end = text.Length - 2_000; end <= text.Length; end += 97)
        {
            var prefix = text[..end];
            var all = MarkdownRenderer.Render(prefix, width, false);
            var expected = all.Skip(Math.Max(0, all.Count - HistoryFormatter.LiveTailLines));

            Assert.Equal(expected, MarkdownRenderer.RenderTail(prefix, width, HistoryFormatter.LiveTailLines, false));
        }
    }

    private static List<string> Texts(IEnumerable<StyledLine> lines)
    {
        return lines.Select(line => line.Text).ToList();
    }

    private static MarkdownStyle StyleOf(StyledLine line, string fragment)
    {
        var index = line.Text.IndexOf(fragment, StringComparison.Ordinal);
        Assert.True(index >= 0, $"\"{fragment}\" is not in \"{line.Text}\".");

        if (line.Styles == null)
        {
            return MarkdownStyle.None;
        }

        var style = line.Styles[index];

        for (var offset = 1; offset < fragment.Length; offset++)
        {
            Assert.Equal(style, line.Styles[index + offset]);
        }

        return (MarkdownStyle)style;
    }
}

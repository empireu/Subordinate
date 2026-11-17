using System.Text;
using Terminal.Gui.Text;

namespace Subordinate.Tui.Presentation;

public static class TextWrap
{
    public static IReadOnlyList<string> Wrap(string text, int width, int continuationIndent)
    {
        if (width < 10)
        {
            width = 10;
        }

        if (continuationIndent < 0)
        {
            continuationIndent = 0;
        }

        if (continuationIndent >= width)
        {
            continuationIndent = Math.Max(0, width - 1);
        }

        var lines = new List<string>();
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (var sourceLine in normalized.Split('\n'))
        {
            WrapLine(sourceLine, width, continuationIndent, lines);
        }

        return lines;
    }

    private static void WrapLine(string source, int width, int indent, List<string> lines)
    {
        if (IsPrintableAscii(source))
        {
            WrapAsciiLine(source, width, indent, lines);
            return;
        }

        var runes = ExpandTabs(source);

        if (runes.Count == 0)
        {
            lines.Add(string.Empty);
            return;
        }

        var start = 0;
        var firstLine = true;
        var builder = new StringBuilder();
        Span<char> encoded = stackalloc char[2];

        while (start < runes.Count)
        {
            var available = firstLine ? width : Math.Max(1, width - indent);
            var end = start;
            var used = 0;
            var lastSpace = -1;

            while (end < runes.Count)
            {
                var item = runes[end];

                if (used + item.Columns > available && end > start)
                {
                    if (item.Rune.Value == ' ')
                    {
                        lastSpace = end;
                    }

                    break;
                }

                if (item.Rune.Value == ' ')
                {
                    lastSpace = end;
                }

                used += item.Columns;
                end++;
            }

            if (end < runes.Count && lastSpace > start)
            {
                end = lastSpace;
            }

            if (end == start)
            {
                end = start + 1;
            }

            builder.Clear();

            for (var index = start; index < end; index++)
            {
                var written = runes[index].Rune.EncodeToUtf16(encoded);
                builder.Append(encoded[..written]);
            }

            var line = builder.ToString().TrimEnd(' ');
            lines.Add(firstLine ? line : new string(' ', indent) + line);
            firstLine = false;
            start = end;

            while (start < runes.Count && runes[start].Rune.Value == ' ')
            {
                start++;
            }
        }
    }

    private static bool IsPrintableAscii(string source)
    {
        foreach (var character in source)
        {
            if (character is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Wraps a line where every character is one column wide, with the same breaks as the general path.
    /// </summary>
    private static void WrapAsciiLine(string source, int width, int indent, List<string> lines)
    {
        if (source.Length <= width)
        {
            lines.Add(source.TrimEnd(' '));
            return;
        }

        var start = 0;
        var firstLine = true;

        while (start < source.Length)
        {
            var available = firstLine ? width : Math.Max(1, width - indent);
            var end = Math.Min(source.Length, start + available);

            if (end < source.Length)
            {
                var lastSpace = source[end] == ' ' ? end : source.LastIndexOf(' ', end - 1, end - start);

                if (lastSpace > start)
                {
                    end = lastSpace;
                }
            }

            var line = source[start..end].TrimEnd(' ');
            lines.Add(firstLine ? line : new string(' ', indent) + line);
            firstLine = false;
            start = end;

            while (start < source.Length && source[start] == ' ')
            {
                start++;
            }
        }
    }

    private static List<(Rune Rune, int Columns)> ExpandTabs(string source)
    {
        var result = new List<(Rune Rune, int Columns)>();
        var column = 0;

        foreach (var rune in source.EnumerateRunes())
        {
            if (rune.Value == '\t')
            {
                var spaces = 4 - (column % 4);

                for (var index = 0; index < spaces; index++)
                {
                    result.Add((new Rune(' '), 1));
                    column++;
                }
            }
            else
            {
                var columns = RuneExtensions.GetColumns(rune);
                result.Add((rune, columns));
                column += columns;
            }
        }

        return result;
    }
}

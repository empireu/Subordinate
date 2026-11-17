using System.Buffers;
using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Terminal.Gui.Text;

namespace Subordinate.Tui.Presentation;

[Flags]
public enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strikethrough = 4,
    Code = 8,
    CodeBlock = 16,
    Heading = 32,
    Marker = 64,
    Link = 128
}

/// <summary>
///     One screen line. Styles holds one <see cref="MarkdownStyle" /> per character of Text, or null when no character is styled.
/// </summary>
public readonly record struct StyledLine(string Text, string? Styles);

/// <summary>
///     Lays out Markdown as wrapped screen lines. Plain output keeps heading marks, backticks and code fences, styled output leaves them to colors.
/// </summary>
public static class MarkdownRenderer
{
    private const int MinimumWidth = 10;
    private const int MaximumDepth = 24;
    private const int TabSize = 4;
    private const int MinimumColumnWidth = 3;
    private const string ColumnSeparator = " | ";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .DisableHtml()
        .Build();

    public static List<StyledLine> Render(string markdown, int width, bool plain)
    {
        width = Math.Max(MinimumWidth, width);

        try
        {
            var document = Markdown.Parse(markdown, Pipeline);
            var writer = new Writer(markdown, width, plain);
            writer.WriteBlocks(document, Prefix.Empty, Prefix.Empty, false, 0);
            return writer.Lines;
        }
        catch (Exception)
        {
            return TextWrap.Wrap(markdown, width, 0).Select(line => new StyledLine(line, null)).ToList();
        }
    }

    /// <summary>
    ///     Renders the last lines of a growing text by parsing only from a blank line near its end, reopening a code fence the cut would lose. The text is sanitized here.
    /// </summary>
    public static List<StyledLine> RenderTail(string text, int width, int count, bool plain)
    {
        var needed = (long)(count + 1) * Math.Max(MinimumWidth, width);

        while (true)
        {
            var start = text.Length > needed ? CutPoint(text, (int)(text.Length - needed)) : 0;
            var source = TerminalText.Sanitize(start == 0 ? text : text[start..]);
            var fence = start == 0 ? null : OpenFence(text, start);

            if (fence != null)
            {
                source = fence + "\n" + source;
            }

            var lines = Render(source, width, plain);

            if (lines.Count >= count || start == 0)
            {
                return lines.Count > count ? lines.GetRange(lines.Count - count, count) : lines;
            }

            needed *= 2;
        }
    }

    private static int CutPoint(string text, int limit)
    {
        var index = text.LastIndexOf("\n\n", limit, StringComparison.Ordinal);
        return index < 0 ? 0 : index + 2;
    }

    /// <summary>
    ///     Returns the opening line of a code fence that is still open at the end position, or null.
    /// </summary>
    private static string? OpenFence(string text, int end)
    {
        var head = text.AsSpan(0, end);

        if (head.IndexOf("```", StringComparison.Ordinal) < 0 && head.IndexOf("~~~", StringComparison.Ordinal) < 0)
        {
            return null;
        }

        string? open = null;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var position = 0;

        while (position < end)
        {
            var lineEnd = text.IndexOf('\n', position, end - position);

            if (lineEnd < 0)
            {
                lineEnd = end;
            }

            var line = text.AsSpan(position, lineEnd - position).TrimEnd('\r');
            var indent = 0;

            while (indent < line.Length && indent < 3 && line[indent] == ' ')
            {
                indent++;
            }

            var body = line[indent..];

            if (body.Length >= 3 && body[0] is '`' or '~')
            {
                var character = body[0];
                var run = 0;

                while (run < body.Length && body[run] == character)
                {
                    run++;
                }

                if (open == null)
                {
                    if (run >= 3 && (character == '~' || body[run..].IndexOf('`') < 0))
                    {
                        open = line.ToString();
                        fenceCharacter = character;
                        fenceLength = run;
                    }
                }
                else if (character == fenceCharacter && run >= fenceLength && body[run..].IsWhiteSpace())
                {
                    open = null;
                }
            }

            position = lineEnd + 1;
        }

        return open == null ? null : TerminalText.Sanitize(open);
    }

    private static int RuneAt(string text, int index, out int columns)
    {
        var character = text[index];

        if (character is >= ' ' and <= '~')
        {
            columns = 1;
            return 1;
        }

        if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != OperationStatus.Done)
        {
            columns = 1;
            return 1;
        }

        columns = Math.Max(0, rune.GetColumns());
        return consumed;
    }

    private static int Columns(string text, int start, int end)
    {
        var columns = 0;

        while (start < end)
        {
            start += RuneAt(text, start, out var runeColumns);
            columns += runeColumns;
        }

        return columns;
    }

    /// <summary>
    ///     Splits text into lines of at most the available columns, breaking at the last space like <see cref="TextWrap" />.
    /// </summary>
    private static void BreakLines(string text, int start, int end, int available, List<(int Start, int End)> pieces)
    {
        available = Math.Max(1, available);

        if (start == end)
        {
            pieces.Add((start, end));
            return;
        }

        while (start < end)
        {
            var position = start;
            var used = 0;
            var lastSpace = -1;

            while (position < end)
            {
                var consumed = RuneAt(text, position, out var columns);

                if (used + columns > available && position > start)
                {
                    if (text[position] == ' ')
                    {
                        lastSpace = position;
                    }

                    break;
                }

                if (text[position] == ' ')
                {
                    lastSpace = position;
                }

                used += columns;
                position += consumed;
            }

            if (position < end && lastSpace > start)
            {
                position = lastSpace;
            }

            var trimmed = position;

            while (trimmed > start && text[trimmed - 1] == ' ')
            {
                trimmed--;
            }

            pieces.Add((start, trimmed));
            start = position;

            while (start < end && text[start] == ' ')
            {
                start++;
            }
        }
    }

    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t'))
        {
            return line;
        }

        var builder = new StringBuilder(line.Length + 8);

        foreach (var character in line)
        {
            if (character == '\t')
            {
                builder.Append(' ', TabSize - (builder.Length % TabSize));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string StyleRun(MarkdownStyle style, int length)
    {
        return new string((char)style, length);
    }

    private readonly record struct Prefix(string Text, string Styles)
    {
        public static readonly Prefix Empty = new(string.Empty, string.Empty);

        public int Columns => Text.Length;

        public Prefix Add(string text, MarkdownStyle style)
        {
            return new Prefix(Text + text, Styles + StyleRun(style, text.Length));
        }
    }

    private sealed class StyledBuffer
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _styles = new();

        public int Length => _text.Length;

        public string Text => _text.ToString();

        public string Styles => _styles.ToString();

        public void Append(string text, MarkdownStyle style)
        {
            var start = _text.Length;
            _text.Append(text);
            _text.Replace('\t', ' ', start, text.Length);
            _styles.Append((char)style, text.Length);
        }

        public string TextFrom(int start)
        {
            return _text.ToString(start, _text.Length - start);
        }

        public void JoinLines()
        {
            _text.Replace('\n', ' ');
        }

        public void TrimTrailingLineBreaks()
        {
            while (_text.Length > 0 && _text[^1] == '\n')
            {
                _text.Length--;
                _styles.Length--;
            }
        }
    }

    private sealed class Writer
    {
        private readonly string _source;
        private readonly int _width;
        private readonly bool _plain;
        private readonly List<(int Start, int End)> _pieces = [];

        public Writer(string source, int width, bool plain)
        {
            _source = source;
            _width = width;
            _plain = plain;
        }

        public List<StyledLine> Lines { get; } = [];

        public void WriteBlocks(ContainerBlock container, Prefix first, Prefix rest, bool tight, int depth)
        {
            var written = false;

            foreach (var block in container)
            {
                if (block is LinkReferenceDefinitionGroup)
                {
                    continue;
                }

                if (written && !tight)
                {
                    Emit(rest, string.Empty, string.Empty, false);
                }

                WriteBlock(block, written ? rest : first, rest, depth);
                written = true;
            }

            if (!written && first.Text != rest.Text)
            {
                Emit(first, string.Empty, string.Empty, false);
            }
        }

        private void WriteBlock(Block block, Prefix first, Prefix rest, int depth)
        {
            if (depth > MaximumDepth || _width - rest.Columns < MinimumWidth / 2)
            {
                WriteRaw(block, Prefix.Empty, Prefix.Empty);
                return;
            }

            switch (block)
            {
                case HeadingBlock heading:
                {
                    var buffer = new StyledBuffer();

                    if (_plain)
                    {
                        buffer.Append(new string('#', Math.Max(1, heading.Level)) + " ", MarkdownStyle.Marker);
                    }

                    WriteInlines(heading.Inline, buffer, MarkdownStyle.Heading);
                    Wrap(buffer, first, rest);
                    break;
                }
                case ParagraphBlock paragraph:
                {
                    var buffer = new StyledBuffer();
                    WriteInlines(paragraph.Inline, buffer, MarkdownStyle.None);
                    Wrap(buffer, first, rest);
                    break;
                }
                case CodeBlock code:
                {
                    WriteCode(code, first, rest);
                    break;
                }
                case QuoteBlock quote:
                {
                    var bar = _plain ? "> " : "| ";
                    WriteBlocks(quote, first.Add(bar, MarkdownStyle.Marker), rest.Add(bar, MarkdownStyle.Marker), false, depth + 1);
                    break;
                }
                case ListBlock list:
                {
                    WriteList(list, first, rest, depth);
                    break;
                }
                case ThematicBreakBlock:
                {
                    var rule = new string('-', Math.Max(1, _width - first.Columns));
                    Emit(first, rule, StyleRun(MarkdownStyle.Marker, rule.Length), false);
                    break;
                }
                case Table table:
                {
                    WriteTable(table, first, rest);
                    break;
                }
                case ContainerBlock container:
                {
                    WriteBlocks(container, first, rest, false, depth + 1);
                    break;
                }
                default:
                {
                    WriteRaw(block, first, rest);
                    break;
                }
            }
        }

        private void WriteRaw(Block block, Prefix first, Prefix rest)
        {
            var start = Math.Clamp(block.Span.Start, 0, _source.Length);
            var length = Math.Clamp(block.Span.Length, 0, _source.Length - start);
            var buffer = new StyledBuffer();
            buffer.Append(_source.Substring(start, length), MarkdownStyle.None);
            Wrap(buffer, first, rest);
        }

        private void WriteCode(CodeBlock code, Prefix first, Prefix rest)
        {
            var fenced = code as FencedCodeBlock;
            var prefix = first;
            string? fence = null;

            if (_plain && fenced != null)
            {
                fence = new string(fenced.FencedChar == '~' ? '~' : '`', Math.Max(3, fenced.OpeningFencedCharCount));
                var opening = fence + (fenced.Info ?? string.Empty);
                Emit(prefix, opening, StyleRun(MarkdownStyle.Marker, opening.Length), false);
                prefix = rest;
            }

            var panelRest = _plain ? rest : rest.Add(" ", MarkdownStyle.CodeBlock);
            var lines = code.Lines;

            for (var index = 0; index < lines.Count; index++)
            {
                var text = ExpandTabs(lines.Lines[index].Slice.ToString());
                var linePrefix = _plain ? prefix : prefix.Add(" ", MarkdownStyle.CodeBlock);
                WriteText(text, StyleRun(MarkdownStyle.CodeBlock, text.Length), linePrefix, panelRest, true);
                prefix = rest;
            }

            if (fence != null)
            {
                Emit(rest, fence, StyleRun(MarkdownStyle.Marker, fence.Length), false);
            }
        }

        private void WriteList(ListBlock list, Prefix first, Prefix rest, int depth)
        {
            long number = 1;

            if (list.IsOrdered && !long.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            {
                number = 1;
            }

            var delimiter = list.OrderedDelimiter == ')' ? ')' : '.';
            var labelWidth = list.IsOrdered ? (number + list.Count - 1).ToString(CultureInfo.InvariantCulture).Length + 2 : 2;
            var indent = new string(' ', labelWidth);
            var written = false;

            foreach (var block in list)
            {
                if (block is not ListItemBlock item)
                {
                    continue;
                }

                if (written && list.IsLoose)
                {
                    Emit(rest, string.Empty, string.Empty, false);
                }

                var label = list.IsOrdered
                    ? (number.ToString(CultureInfo.InvariantCulture) + delimiter).PadLeft(labelWidth - 1) + " "
                    : "- ";
                WriteBlocks(item, (written ? rest : first).Add(label, MarkdownStyle.Marker), rest.Add(indent, MarkdownStyle.None), !list.IsLoose, depth + 1);
                number++;
                written = true;
            }
        }

        private void WriteTable(Table table, Prefix first, Prefix rest)
        {
            var rows = new List<(bool Header, List<(string Text, string Styles)> Cells)>();
            var columnCount = 0;

            foreach (var rowBlock in table)
            {
                if (rowBlock is not TableRow row)
                {
                    continue;
                }

                var cells = new List<(string Text, string Styles)>();

                foreach (var cellBlock in row)
                {
                    if (cellBlock is not TableCell cell)
                    {
                        continue;
                    }

                    var buffer = new StyledBuffer();

                    foreach (var inner in cell)
                    {
                        if (inner is LeafBlock { Inline: not null } leaf)
                        {
                            if (buffer.Length > 0)
                            {
                                buffer.Append(" ", MarkdownStyle.None);
                            }

                            WriteInlines(leaf.Inline, buffer, row.IsHeader ? MarkdownStyle.Bold : MarkdownStyle.None);
                        }
                    }

                    buffer.JoinLines();
                    cells.Add((buffer.Text, buffer.Styles));
                }

                columnCount = Math.Max(columnCount, cells.Count);
                rows.Add((row.IsHeader, cells));
            }

            if (rows.Count == 0 || columnCount == 0)
            {
                return;
            }

            var natural = new int[columnCount];

            foreach (var (_, cells) in rows)
            {
                for (var column = 0; column < cells.Count; column++)
                {
                    natural[column] = Math.Max(natural[column], Columns(cells[column].Text, 0, cells[column].Text.Length));
                }
            }

            var widths = FitColumns(natural, _width - Math.Max(first.Columns, rest.Columns));
            var prefix = first;

            if (widths == null)
            {
                foreach (var (_, cells) in rows)
                {
                    var buffer = new StyledBuffer();

                    for (var column = 0; column < cells.Count; column++)
                    {
                        if (column > 0)
                        {
                            buffer.Append(ColumnSeparator, MarkdownStyle.Marker);
                        }

                        AppendStyled(buffer, cells[column].Text, cells[column].Styles);
                    }

                    Wrap(buffer, prefix, rest);
                    prefix = rest;
                }

                return;
            }

            var cellPieces = new List<(int Start, int End)>[columnCount];

            foreach (var (header, cells) in rows)
            {
                var height = 1;

                for (var column = 0; column < columnCount; column++)
                {
                    cellPieces[column] = [];

                    if (column < cells.Count)
                    {
                        BreakLines(cells[column].Text, 0, cells[column].Text.Length, widths[column], cellPieces[column]);
                    }

                    height = Math.Max(height, cellPieces[column].Count);
                }

                for (var line = 0; line < height; line++)
                {
                    var text = new StringBuilder();
                    var styles = new StringBuilder();

                    for (var column = 0; column < columnCount; column++)
                    {
                        if (column > 0)
                        {
                            text.Append(ColumnSeparator);
                            styles.Append((char)MarkdownStyle.Marker, ColumnSeparator.Length);
                        }

                        var cellText = string.Empty;
                        var cellStyles = string.Empty;

                        if (column < cells.Count && line < cellPieces[column].Count)
                        {
                            var (start, end) = cellPieces[column][line];
                            cellText = cells[column].Text[start..end];
                            cellStyles = cells[column].Styles[start..end];
                        }

                        var padding = Math.Max(0, widths[column] - Columns(cellText, 0, cellText.Length));
                        var before = Alignment(table, column) switch
                        {
                            TableColumnAlign.Right => padding,
                            TableColumnAlign.Center => padding / 2,
                            _ => 0
                        };
                        text.Append(' ', before).Append(cellText).Append(' ', padding - before);
                        styles.Append((char)MarkdownStyle.None, before).Append(cellStyles).Append((char)MarkdownStyle.None, padding - before);
                    }

                    Emit(prefix, text.ToString(), styles.ToString(), false);
                    prefix = rest;
                }

                if (header)
                {
                    var rule = string.Join("-+-", widths.Select(columnWidth => new string('-', columnWidth)));
                    Emit(rest, rule, StyleRun(MarkdownStyle.Marker, rule.Length), false);
                }
            }
        }

        private static TableColumnAlign? Alignment(Table table, int column)
        {
            return column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment : null;
        }

        /// <summary>
        ///     Shrinks the widest columns first until the table fits, or returns null when even narrow columns would not fit.
        /// </summary>
        private static int[]? FitColumns(int[] natural, int available)
        {
            var count = natural.Length;
            var budget = available - ColumnSeparator.Length * (count - 1);
            var widths = natural.Select(width => Math.Max(1, width)).ToArray();

            if (widths.Sum() <= budget)
            {
                return widths;
            }

            if (budget < count * MinimumColumnWidth)
            {
                return null;
            }

            var order = Enumerable.Range(0, count).OrderBy(column => widths[column]).ToArray();
            var remaining = budget;

            for (var index = 0; index < count; index++)
            {
                var column = order[index];
                var share = remaining / (count - index);
                widths[column] = Math.Min(widths[column], share);
                remaining -= widths[column];
            }

            return widths;
        }

        private void WriteInlines(ContainerInline? container, StyledBuffer buffer, MarkdownStyle style)
        {
            if (container == null)
            {
                return;
            }

            for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                    {
                        buffer.Append(literal.Content.ToString(), style);
                        break;
                    }
                    case CodeInline code:
                    {
                        buffer.Append(_plain ? "`" + code.Content + "`" : code.Content, style | MarkdownStyle.Code);
                        break;
                    }
                    case LineBreakInline:
                    {
                        buffer.Append("\n", style);
                        break;
                    }
                    case AutolinkInline autolink:
                    {
                        buffer.Append(autolink.Url, style | MarkdownStyle.Link);
                        break;
                    }
                    case LinkInline link:
                    {
                        var start = buffer.Length;
                        WriteInlines(link, buffer, style | MarkdownStyle.Link);

                        if (!string.IsNullOrEmpty(link.Url) && buffer.TextFrom(start) != link.Url)
                        {
                            buffer.Append(" (" + link.Url + ")", MarkdownStyle.Marker);
                        }

                        break;
                    }
                    case EmphasisInline emphasis:
                    {
                        WriteInlines(emphasis, buffer, style | EmphasisStyle(emphasis));
                        break;
                    }
                    case TaskList task:
                    {
                        buffer.Append(task.Checked ? "[x]" : "[ ]", MarkdownStyle.Marker);
                        break;
                    }
                    case HtmlEntityInline entity:
                    {
                        buffer.Append(entity.Transcoded.ToString(), style);
                        break;
                    }
                    case HtmlInline html:
                    {
                        buffer.Append(html.Tag, style);
                        break;
                    }
                    case DelimiterInline delimiter:
                    {
                        buffer.Append(delimiter.ToLiteral(), style);
                        WriteInlines(delimiter, buffer, style);
                        break;
                    }
                    case ContainerInline nested:
                    {
                        WriteInlines(nested, buffer, style);
                        break;
                    }
                }
            }
        }

        private static MarkdownStyle EmphasisStyle(EmphasisInline emphasis)
        {
            return emphasis.DelimiterChar switch
            {
                '*' or '_' => emphasis.DelimiterCount >= 2 ? MarkdownStyle.Bold : MarkdownStyle.Italic,
                '~' => emphasis.DelimiterCount >= 2 ? MarkdownStyle.Strikethrough : MarkdownStyle.None,
                _ => MarkdownStyle.None
            };
        }

        private static void AppendStyled(StyledBuffer buffer, string text, string styles)
        {
            var start = 0;

            while (start < text.Length)
            {
                var end = start + 1;

                while (end < text.Length && styles[end] == styles[start])
                {
                    end++;
                }

                buffer.Append(text[start..end], (MarkdownStyle)styles[start]);
                start = end;
            }
        }

        /// <summary>
        ///     Wraps a paragraph, starting a new line at each line break. Only the first line gets the first prefix.
        /// </summary>
        private void Wrap(StyledBuffer buffer, Prefix first, Prefix rest)
        {
            buffer.TrimTrailingLineBreaks();
            var text = buffer.Text;
            var styles = buffer.Styles;
            var prefix = first;
            var segmentStart = 0;

            while (true)
            {
                var segmentEnd = text.IndexOf('\n', segmentStart);

                if (segmentEnd < 0)
                {
                    segmentEnd = text.Length;
                }

                WriteText(text[segmentStart..segmentEnd], styles[segmentStart..segmentEnd], prefix, rest, false);
                prefix = rest;

                if (segmentEnd >= text.Length)
                {
                    break;
                }

                segmentStart = segmentEnd + 1;
            }
        }

        private void WriteText(string text, string styles, Prefix first, Prefix rest, bool panel)
        {
            _pieces.Clear();
            BreakLines(text, 0, text.Length, _width - Math.Max(first.Columns, rest.Columns), _pieces);

            for (var index = 0; index < _pieces.Count; index++)
            {
                var (start, end) = _pieces[index];
                Emit(index == 0 ? first : rest, text[start..end], styles[start..end], panel);
            }
        }

        private void Emit(Prefix prefix, string text, string styles, bool panel)
        {
            var lineText = prefix.Text + text;
            var lineStyles = prefix.Styles + styles;

            if (panel && !_plain)
            {
                var padding = _width - prefix.Columns - Columns(text, 0, text.Length);

                if (padding > 0)
                {
                    lineText += new string(' ', padding);
                    lineStyles += StyleRun(MarkdownStyle.CodeBlock, padding);
                }
            }
            else
            {
                var length = lineText.Length;

                while (length > 0 && lineText[length - 1] == ' ')
                {
                    length--;
                }

                lineText = lineText[..length];
                lineStyles = lineStyles[..length];
            }

            Lines.Add(new StyledLine(lineText, lineStyles.AsSpan().IndexOfAnyExcept((char)MarkdownStyle.None) < 0 ? null : lineStyles));
        }
    }
}

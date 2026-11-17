namespace Subordinate.Tui.Presentation;

public sealed record HistoryLine(string Text, HistoryBlockKind Kind, HistoryBlockRole Role);

public static class HistoryLayout
{
    public const int BodyIndent = 2;

    /// <summary>
    ///     Wraps blocks into screen lines. Plain output draws headers as dashed rules, colored output leaves them to a background color.
    /// </summary>
    public static void Append(IEnumerable<HistoryBlock> blocks, int width, bool plain, List<HistoryLine> lines)
    {
        foreach (var block in blocks)
        {
            switch (block.Role)
            {
                case HistoryBlockRole.Spacer:
                {
                    if (lines.Count == 0 || lines[^1].Role != HistoryBlockRole.Spacer)
                    {
                        lines.Add(new HistoryLine(string.Empty, block.Kind, HistoryBlockRole.Spacer));
                    }

                    break;
                }
                case HistoryBlockRole.Header:
                {
                    AppendHeader(block, width, plain, lines);
                    break;
                }
                default:
                {
                    var indent = new string(' ', BodyIndent);

                    foreach (var line in TextWrap.Wrap(block.Text, Math.Max(1, width - BodyIndent), 0))
                    {
                        lines.Add(new HistoryLine(line.Length == 0 ? string.Empty : indent + line, block.Kind, block.Role));
                    }

                    break;
                }
            }
        }
    }

    private static void AppendHeader(HistoryBlock block, int width, bool plain, List<HistoryLine> lines)
    {
        var text = plain ? "-- " + block.Text : " " + block.Text;
        var wrapped = TextWrap.Wrap(text, width, plain ? 3 : 1);

        for (var index = 0; index < wrapped.Count; index++)
        {
            var line = wrapped[index];

            if (plain && index == wrapped.Count - 1 && line.Length + 2 <= width)
            {
                line = line + " " + new string('-', width - line.Length - 1);
            }

            lines.Add(new HistoryLine(line, block.Kind, HistoryBlockRole.Header));
        }
    }
}
